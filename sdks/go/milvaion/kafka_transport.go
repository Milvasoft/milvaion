package milvaion

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"sort"
	"strconv"
	"sync"
	"sync/atomic"
	"time"

	"github.com/twmb/franz-go/pkg/kadm"
	"github.com/twmb/franz-go/pkg/kerr"
	"github.com/twmb/franz-go/pkg/kgo"
	"github.com/twmb/franz-go/pkg/kmsg"
)

var errKafkaNotConnected = errors.New("milvaion: not connected to Kafka")

const (
	kafkaCommitInterval = 500 * time.Millisecond
	kafkaMaxPollRecords = 1000
)

// kafkaTransport runs a worker on Kafka (docs/githubdocs/KAFKA-TRANSPORT.md). One consumer group
// member reads the worker's job and retry topics; records are buffered per partition, dispatched
// up to MaxParallelJobs (and MaxInFlightPerPartition per partition) and committed in order once
// their outcome has been reported.
type kafkaTransport struct {
	w      *Worker
	cfg    resolvedKafka
	topics kafkaTopics
	logger *slog.Logger

	clientMu sync.RWMutex
	producer *kgo.Client

	mu       sync.Mutex
	consumer *kgo.Client
	assigned map[topicPartition]bool
	parts    map[topicPartition]*partitionState
	running  int
	cursor   int
	stopping bool

	pollCancel context.CancelFunc
	pollDone   chan struct{}
	commitStop chan struct{}
	commitDone chan struct{}

	heartbeatFailed atomic.Bool
}

type topicPartition struct {
	topic     string
	partition int32
}

// partitionState is what this instance holds of one assigned partition.
type partitionState struct {
	tp        topicPartition
	retry     bool
	tracker   *offsetTracker
	backlog   []kafkaRecord // taken from the client, not dispatched yet, in offset order
	inFlight  int
	paused    bool
	held      *heldRecord // head of a retry partition that is not due yet
	committed int64
}

type kafkaRecord struct {
	rec     *kgo.Record
	headers map[string]any
}

type heldRecord struct {
	offset int64
	timer  *time.Timer
	stop   chan struct{}
}

func newKafkaTransport(w *Worker) *kafkaTransport {
	return &kafkaTransport{
		w:        w,
		cfg:      w.opts.kafka,
		topics:   newKafkaTopics(w.opts.kafka.topicPrefix, w.opts.workerID),
		logger:   w.opts.logger,
		assigned: map[topicPartition]bool{},
		parts:    map[topicPartition]*partitionState{},
	}
}

func (t *kafkaTransport) clientOptions() []kgo.Opt {
	opts := []kgo.Opt{
		kgo.SeedBrokers(t.cfg.brokers...),
		kgo.ClientID(t.w.opts.instanceID),
		// Uncompressed, so every client library of the deployment can read what this worker writes.
		kgo.ProducerBatchCompression(kgo.NoCompression()),
	}

	if t.cfg.tls != nil {
		opts = append(opts, kgo.DialTLSConfig(t.cfg.tls.Clone()))
	}

	if t.cfg.sasl != nil {
		opts = append(opts, kgo.SASL(t.cfg.sasl))
	}

	return opts
}

func (t *kafkaTransport) start(ctx, startCtx context.Context) error {
	producer, err := kgo.NewClient(t.clientOptions()...)

	if err != nil {
		return fmt.Errorf("milvaion: creating the Kafka client: %w", err)
	}

	fail := func(err error) error {
		t.clientMu.Lock()
		t.producer = nil
		t.clientMu.Unlock()
		producer.Close()

		if ctx.Err() != nil {
			return ctx.Err()
		}

		return err
	}

	var lastErr error

	for {
		if lastErr = producer.Ping(startCtx); lastErr == nil {
			break
		}

		t.logger.Warn("could not connect to Kafka", "brokers", t.cfg.brokers, "error", lastErr)

		if sleepContext(startCtx, time.Second) != nil {
			return fail(fmt.Errorf("milvaion: could not connect to Kafka within %s: %w", t.w.opts.startupTimeout, lastErr))
		}
	}

	if t.cfg.autoCreateTopics {
		if err := t.createTopics(startCtx, producer); err != nil {
			return fail(err)
		}
	}

	t.clientMu.Lock()
	t.producer = producer
	t.clientMu.Unlock()

	if err := t.w.register(startCtx); err != nil {
		return fail(fmt.Errorf("milvaion: %w", err))
	}

	t.w.sendHeartbeat(false)
	t.logger.Info("registered", "worker", t.w.opts.workerID, "transport", "kafka")

	consumer, err := kgo.NewClient(append(t.clientOptions(),
		kgo.ConsumerGroup(t.topics.group),
		kgo.ConsumeTopics(t.topics.jobs, t.topics.retry),
		kgo.DisableAutoCommit(),
		kgo.ConsumeResetOffset(kgo.NewOffset().AtStart()),
		kgo.Balancers(kgo.CooperativeStickyBalancer()),
		kgo.OnPartitionsAssigned(t.onAssigned),
		kgo.OnPartitionsRevoked(t.onRevoked),
		kgo.OnPartitionsLost(t.onLost),
	)...)

	if err != nil {
		return fail(fmt.Errorf("milvaion: creating the Kafka consumer: %w", err))
	}

	pollCtx, pollCancel := context.WithCancel(context.Background())

	t.mu.Lock()
	t.consumer = consumer
	t.pollCancel = pollCancel
	t.pollDone = make(chan struct{})
	t.commitStop = make(chan struct{})
	t.commitDone = make(chan struct{})
	t.mu.Unlock()

	go t.pollLoop(pollCtx)
	go t.commitLoop()

	t.logger.Info("consuming", "topics", []string{t.topics.jobs, t.topics.retry}, "group", t.topics.group,
		"maxParallelJobs", t.w.opts.maxParallelJobs, "maxInFlightPerPartition", t.cfg.maxInFlightPerPartition)

	return nil
}

// createTopics creates the worker's job and retry topics and the shared topics it writes to.
// Topics that exist already are left as they are.
func (t *kafkaTransport) createTopics(ctx context.Context, client *kgo.Client) error {
	admin := kadm.NewClient(client)
	topics := []string{t.topics.jobs, t.topics.retry, t.topics.status, t.topics.logs, t.topics.heartbeats, t.topics.registrations, t.topics.dlq}

	responses, err := admin.CreateTopics(ctx, t.cfg.numPartitions, t.cfg.replicationFactor, nil, topics...)

	if err != nil {
		return fmt.Errorf("milvaion: creating Kafka topics: %w", err)
	}

	for _, r := range responses.Sorted() {
		if r.Err != nil && !errors.Is(r.Err, kerr.TopicAlreadyExists) {
			return fmt.Errorf("milvaion: creating Kafka topic %s: %w", r.Topic, r.Err)
		}
	}

	return nil
}

func (t *kafkaTransport) producerClient() *kgo.Client {
	t.clientMu.RLock()
	defer t.clientMu.RUnlock()

	return t.producer
}

func (t *kafkaTransport) produce(ctx context.Context, record *kgo.Record) error {
	client := t.producerClient()

	if client == nil {
		return errKafkaNotConnected
	}

	return client.ProduceSync(ctx, record).FirstErr()
}

// produceReliably retries a produce whose failure would otherwise leave the record's offset stuck.
func (t *kafkaTransport) produceReliably(record *kgo.Record) error {
	var err error

	for attempt, delay := range []time.Duration{0, time.Second, 2 * time.Second, 5 * time.Second} {
		if attempt > 0 {
			time.Sleep(delay)
		}

		ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
		err = t.produce(ctx, record)
		cancel()

		if err == nil || errors.Is(err, errKafkaNotConnected) {
			return err
		}
	}

	return err
}

// publish maps the processor's RabbitMQ-named destinations to the Kafka topics.
func (t *kafkaTransport) publish(ctx context.Context, exchange, routingKey string, body []byte, o publishOptions) error {
	topic := ""

	switch {
	case exchange == exchangeDeadLetter:
		topic = t.topics.dlq
	case exchange == "" && routingKey == queueStatusUpdates:
		topic = t.topics.status
	case exchange == "" && routingKey == queueWorkerLogs:
		topic = t.topics.logs
	case exchange == "" && routingKey == queueWorkerHeartbeat:
		topic = t.topics.heartbeats
	case exchange == "" && routingKey == queueWorkerRegistration:
		topic = t.topics.registrations
	default:
		return fmt.Errorf("milvaion: no Kafka topic for exchange %q and routing key %q", exchange, routingKey)
	}

	key := stringOr(o.key, o.correlationID)
	headers := map[string]any{}

	for k, v := range o.headers {
		headers[k] = v
	}

	if o.correlationID != "" {
		headers[headerOccurrenceID] = o.correlationID
		headers[headerCorrelationID] = o.correlationID
	}

	record := &kgo.Record{Topic: topic, Value: body, Headers: kafkaRecordHeaders(headers)}

	if key != "" {
		record.Key = []byte(key)
	}

	err := t.produce(ctx, record)

	if routingKey == queueWorkerHeartbeat && exchange == "" {
		t.afterHeartbeat(err)
	}

	return err
}

// afterHeartbeat registers again once heartbeats get through after failing, the Kafka counterpart
// of registering after a RabbitMQ reconnect: the API may have dropped the instance meanwhile.
func (t *kafkaTransport) afterHeartbeat(err error) {
	if err != nil {
		if !errors.Is(err, errKafkaNotConnected) {
			t.heartbeatFailed.Store(true)
		}

		return
	}

	if t.heartbeatFailed.Swap(false) {
		go func() {
			ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
			defer cancel()

			if err := t.w.register(ctx); err != nil {
				t.logger.Warn("could not register again after Kafka recovered", "error", err)
				t.heartbeatFailed.Store(true)
			}
		}()
	}
}

func (t *kafkaTransport) pollLoop(ctx context.Context) {
	defer close(t.pollDone)

	for {
		fetches := t.consumer.PollRecords(ctx, kafkaMaxPollRecords)

		if ctx.Err() != nil || fetches.IsClientClosed() {
			return
		}

		failed := false

		fetches.EachError(func(topic string, partition int32, err error) {
			if errors.Is(err, context.Canceled) {
				return
			}

			failed = true
			t.logger.Warn("Kafka fetch failed", "topic", topic, "partition", partition, "error", err)
		})

		records := fetches.Records()

		if len(records) == 0 {
			if failed && sleepContext(ctx, time.Second) != nil {
				return
			}

			continue
		}

		t.accept(records)
	}
}

// accept buffers polled records per partition and dispatches what fits.
func (t *kafkaTransport) accept(records []*kgo.Record) {
	t.mu.Lock()
	defer t.mu.Unlock()

	if t.stopping {
		return
	}

	for _, rec := range records {
		tp := topicPartition{rec.Topic, rec.Partition}

		// A poll can still hold records of a partition revoked since; its new owner reads them.
		if !t.assigned[tp] {
			continue
		}

		ps := t.parts[tp]

		if ps == nil {
			ps = &partitionState{tp: tp, retry: rec.Topic == t.topics.retry, tracker: newOffsetTracker(), committed: -1}
			t.parts[tp] = ps
		}

		if ps.tracker.add(rec.Offset) {
			ps.backlog = append(ps.backlog, kafkaRecord{rec: rec, headers: kafkaHeaderMap(rec.Headers)})
		}
	}

	t.dispatchLocked()
}

func (t *kafkaTransport) dispatch() {
	t.mu.Lock()
	defer t.mu.Unlock()

	t.dispatchLocked()
}

// dispatchLocked starts buffered records while instance and partition slots are free, holds retry
// records that are not due, and pauses every partition that has records waiting here, so the
// client keeps polling (and the member stays in the group) without buffering more.
func (t *kafkaTransport) dispatchLocked() {
	if t.stopping || t.consumer == nil {
		return
	}

	states := make([]*partitionState, 0, len(t.parts))

	for _, ps := range t.parts {
		states = append(states, ps)
	}

	sort.Slice(states, func(i, j int) bool {
		if states[i].tp.topic != states[j].tp.topic {
			return states[i].tp.topic < states[j].tp.topic
		}

		return states[i].tp.partition < states[j].tp.partition
	})

	pause, resume := map[string][]int32{}, map[string][]int32{}
	now := time.Now()
	start := 0

	if len(states) > 0 {
		start = t.cursor % len(states)
		t.cursor++
	}

	for i := range states {
		ps := states[(start+i)%len(states)]

		for t.running < t.w.opts.maxParallelJobs && ps.inFlight < t.cfg.maxInFlightPerPartition && len(ps.backlog) > 0 {
			next := ps.backlog[0]

			if ps.retry {
				if due, ok := kafkaNotBefore(next.headers); ok && due.After(now) {
					t.holdLocked(ps, next, due)
					break
				}
			}

			t.releaseHoldLocked(ps)
			ps.backlog[0] = kafkaRecord{}
			ps.backlog = ps.backlog[1:]
			ps.inFlight++
			t.running++

			go t.run(ps, next)
		}

		if len(ps.backlog) == 0 {
			ps.backlog = nil
		}

		if wantPaused := len(ps.backlog) > 0; wantPaused != ps.paused {
			ps.paused = wantPaused

			if wantPaused {
				pause[ps.tp.topic] = append(pause[ps.tp.topic], ps.tp.partition)
			} else {
				resume[ps.tp.topic] = append(resume[ps.tp.topic], ps.tp.partition)
			}
		}
	}

	if len(pause) > 0 {
		t.consumer.PauseFetchPartitions(pause)
	}

	if len(resume) > 0 {
		t.consumer.ResumeFetchPartitions(resume)
	}
}

// holdLocked keeps the head of a retry partition until it is due, sending the occurrence's job
// heartbeats meanwhile, and dispatches again when it is.
func (t *kafkaTransport) holdLocked(ps *partitionState, kr kafkaRecord, due time.Time) {
	if ps.held != nil && ps.held.offset == kr.rec.Offset {
		return
	}

	t.releaseHoldLocked(ps)

	held := &heldRecord{offset: kr.rec.Offset, stop: make(chan struct{})}
	held.timer = time.AfterFunc(time.Until(due), t.dispatch)
	ps.held = held

	var scheduled ScheduledJob

	if json.Unmarshal(kr.rec.Value, &scheduled) != nil || scheduled.ID == "" {
		return
	}

	occurrenceID := parseOccurrenceID(kr.headers, "")

	if occurrenceID == "" {
		return
	}

	run := &jobRun{p: t.w.proc, job: scheduled, occurrenceID: occurrenceID, retryCount: parseRetryCount(kr.headers)}

	go func() {
		select {
		case <-held.stop:
			return
		default:
		}

		run.status(StatusRunning, statusFields{}, false)
		t.w.proc.heartbeatWhileWaiting(run, due, held.stop)
	}()
}

func (t *kafkaTransport) releaseHoldLocked(ps *partitionState) {
	if ps.held == nil {
		return
	}

	ps.held.timer.Stop()
	close(ps.held.stop)
	ps.held = nil
}

func (t *kafkaTransport) run(ps *partitionState, kr kafkaRecord) {
	defer t.release(ps)

	rec := kr.rec

	t.w.proc.handle(delivery{
		body:    rec.Value,
		headers: kr.headers,
		ack: func() error {
			t.markDone(ps, rec.Offset)
			return nil
		},
		nack: func(requeue bool) error {
			if requeue {
				return t.requeue(ps, kr)
			}

			return t.deadLetterRaw(ps, kr, "rejected by the worker")
		},
		reject: func(reason string) error { return t.deadLetterRaw(ps, kr, reason) },
		scheduleRetry: func(ctx context.Context, r retryRequest) error {
			return t.produceRetry(ctx, kr, r)
		},
	}, &resolvedJob{queue: rec.Topic})
}

// release frees the instance and partition slot of a finished record.
func (t *kafkaTransport) release(ps *partitionState) {
	t.mu.Lock()
	defer t.mu.Unlock()

	ps.inFlight--
	t.running--
	t.dispatchLocked()
}

// markDone lets the record's offset be committed. A partition revoked meanwhile is ignored: its
// new owner runs the record again.
func (t *kafkaTransport) markDone(ps *partitionState, offset int64) {
	t.mu.Lock()
	defer t.mu.Unlock()

	if t.parts[ps.tp] == ps {
		ps.tracker.done(offset)
	}
}

// requeue writes an interrupted job back to its own topic, the counterpart of a RabbitMQ requeue.
func (t *kafkaTransport) requeue(ps *partitionState, kr kafkaRecord) error {
	record := &kgo.Record{Topic: kr.rec.Topic, Key: kr.rec.Key, Value: kr.rec.Value, Headers: append([]kgo.RecordHeader(nil), kr.rec.Headers...)}

	if err := t.produceReliably(record); err != nil {
		return fmt.Errorf("writing the interrupted job back to %s: %w", kr.rec.Topic, err)
	}

	t.markDone(ps, kr.rec.Offset)

	return nil
}

// deadLetterRaw forwards a record the worker cannot process to the dead letter topic unchanged.
func (t *kafkaTransport) deadLetterRaw(ps *partitionState, kr kafkaRecord, reason string) error {
	headers := append([]kgo.RecordHeader(nil), kr.rec.Headers...)
	headers = append(headers,
		kgo.RecordHeader{Key: headerOriginalTopic, Value: []byte(kr.rec.Topic)},
		kgo.RecordHeader{Key: headerDLQReason, Value: []byte(reason)})

	key := kr.rec.Key

	if occurrenceID := parseOccurrenceID(kr.headers, ""); occurrenceID != "" {
		key = []byte(occurrenceID)
	}

	if err := t.produceReliably(&kgo.Record{Topic: t.topics.dlq, Key: key, Value: kr.rec.Value, Headers: headers}); err != nil {
		return fmt.Errorf("forwarding a record to %s: %w", t.topics.dlq, err)
	}

	t.markDone(ps, kr.rec.Offset)

	return nil
}

// produceRetry writes the next attempt to the retry topic, due at r.notBefore.
func (t *kafkaTransport) produceRetry(ctx context.Context, kr kafkaRecord, r retryRequest) error {
	headers := make(map[string]any, len(kr.headers)+5)

	for k, v := range kr.headers {
		headers[k] = v
	}

	headers[headerOccurrenceID] = r.occurrenceID
	headers[headerCorrelationID] = r.occurrenceID
	headers[headerRetryCount] = strconv.Itoa(r.retryCount)
	headers[headerMaxRetries] = strconv.Itoa(r.maxRetries)
	headers[headerNotBefore] = strconv.FormatInt(r.notBefore.UnixMilli(), 10)

	key := []byte(r.jobID)

	if r.jobID == "" {
		key = kr.rec.Key
	}

	return t.produce(ctx, &kgo.Record{Topic: t.topics.retry, Key: key, Value: kr.rec.Value, Headers: kafkaRecordHeaders(headers)})
}

func (t *kafkaTransport) commitLoop() {
	defer close(t.commitDone)

	ticker := time.NewTicker(kafkaCommitInterval)
	defer ticker.Stop()

	for {
		select {
		case <-ticker.C:
			t.commit(nil)
		case <-t.commitStop:
			return
		}
	}
}

// commit commits the finished position of every partition (or only of the given ones) that moved.
func (t *kafkaTransport) commit(only map[string][]int32) {
	t.mu.Lock()

	offsets := map[string]map[int32]kgo.EpochOffset{}
	states := map[topicPartition]*partitionState{}

	for tp, ps := range t.parts {
		if only != nil && !containsPartition(only, tp) {
			continue
		}

		position, ok := ps.tracker.commitPosition()

		if !ok || position <= ps.committed {
			continue
		}

		if offsets[tp.topic] == nil {
			offsets[tp.topic] = map[int32]kgo.EpochOffset{}
		}

		offsets[tp.topic][tp.partition] = kgo.EpochOffset{Epoch: -1, Offset: position}
		states[tp] = ps
	}

	client := t.consumer
	t.mu.Unlock()

	if len(offsets) == 0 || client == nil {
		return
	}

	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()

	client.CommitOffsetsSync(ctx, offsets, func(_ *kgo.Client, _ *kmsg.OffsetCommitRequest, resp *kmsg.OffsetCommitResponse, err error) {
		if err != nil {
			t.logger.Warn("Kafka offset commit failed; will retry", "error", err)
			return
		}

		t.mu.Lock()
		defer t.mu.Unlock()

		for _, topic := range resp.Topics {
			for _, partition := range topic.Partitions {
				tp := topicPartition{topic.Topic, partition.Partition}

				if err := kerr.ErrorForCode(partition.ErrorCode); err != nil {
					t.logger.Warn("Kafka offset commit failed for a partition", "topic", tp.topic, "partition", tp.partition, "error", err)
					continue
				}

				if ps := states[tp]; ps != nil {
					ps.committed = max(ps.committed, offsets[tp.topic][tp.partition].Offset)
				}
			}
		}
	})
}

func containsPartition(set map[string][]int32, tp topicPartition) bool {
	for _, p := range set[tp.topic] {
		if p == tp.partition {
			return true
		}
	}

	return false
}

func (t *kafkaTransport) onAssigned(_ context.Context, client *kgo.Client, assigned map[string][]int32) {
	t.mu.Lock()

	for topic, partitions := range assigned {
		for _, p := range partitions {
			t.assigned[topicPartition{topic, p}] = true
		}
	}

	t.mu.Unlock()

	client.ResumeFetchPartitions(assigned)
	t.logger.Info("Kafka partitions assigned", "partitions", assigned)
}

// onRevoked commits what has finished on the revoked partitions and forgets them. Jobs still
// running on them finish and report, but their offsets are left to the new owner.
func (t *kafkaTransport) onRevoked(_ context.Context, client *kgo.Client, revoked map[string][]int32) {
	t.commit(revoked)
	t.forget(client, revoked)

	if len(revoked) > 0 {
		t.logger.Info("Kafka partitions revoked", "partitions", revoked)
	}
}

func (t *kafkaTransport) onLost(_ context.Context, client *kgo.Client, lost map[string][]int32) {
	t.forget(client, lost)
	t.logger.Warn("Kafka partitions lost", "partitions", lost)
}

func (t *kafkaTransport) forget(client *kgo.Client, partitions map[string][]int32) {
	t.mu.Lock()

	for topic, list := range partitions {
		for _, p := range list {
			tp := topicPartition{topic, p}
			delete(t.assigned, tp)

			if ps := t.parts[tp]; ps != nil {
				t.releaseHoldLocked(ps)
				delete(t.parts, tp)
			}
		}
	}

	t.mu.Unlock()

	client.ResumeFetchPartitions(partitions)
}

func (t *kafkaTransport) stopConsuming() {
	t.mu.Lock()
	t.stopping = true

	for _, ps := range t.parts {
		t.releaseHoldLocked(ps)
	}

	consumer, pollCancel := t.consumer, t.pollCancel
	t.mu.Unlock()

	if consumer == nil {
		return
	}

	consumer.PauseFetchTopics(t.topics.jobs, t.topics.retry)
	pollCancel()
	<-t.pollDone
}

// drained commits what has finished, then leaves the group.
func (t *kafkaTransport) drained() {
	t.mu.Lock()
	consumer := t.consumer
	t.mu.Unlock()

	if consumer == nil {
		return
	}

	close(t.commitStop)
	<-t.commitDone

	t.commit(nil)
	consumer.Close()

	t.mu.Lock()
	t.consumer = nil
	t.mu.Unlock()
}

func (t *kafkaTransport) close() {
	t.clientMu.Lock()
	producer := t.producer
	t.producer = nil
	t.clientMu.Unlock()

	if producer != nil {
		producer.Close()
	}
}
