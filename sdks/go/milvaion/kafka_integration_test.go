package milvaion

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"os"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/twmb/franz-go/pkg/kadm"
	"github.com/twmb/franz-go/pkg/kgo"
)

// Integration tests against a real Kafka. They run only when MILVAION_KAFKA_BROKERS is set, e.g.
//
//	docker run -d --name milvaion-kafka -p 29092:9092 \
//	  -e KAFKA_NODE_ID=1 -e KAFKA_PROCESS_ROLES=broker,controller \
//	  -e KAFKA_LISTENERS=PLAINTEXT://:9092,CONTROLLER://:9093 \
//	  -e KAFKA_ADVERTISED_LISTENERS=PLAINTEXT://localhost:29092 \
//	  -e KAFKA_CONTROLLER_LISTENER_NAMES=CONTROLLER \
//	  -e KAFKA_LISTENER_SECURITY_PROTOCOL_MAP=CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT \
//	  -e KAFKA_CONTROLLER_QUORUM_VOTERS=1@localhost:9093 \
//	  -e KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR=1 -e KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR=1 \
//	  -e KAFKA_TRANSACTION_STATE_LOG_MIN_ISR=1 -e KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS=0 \
//	  apache/kafka:3.8.0
//	MILVAION_KAFKA_BROKERS=localhost:29092 go test -run Kafka ./...
//
// Every test uses its own topic prefix, so the tests do not see each other's records.

type kafkaFixture struct {
	t       *testing.T
	brokers []string
	prefix  string
	topics  kafkaTopics
	worker  *Worker
	client  *kgo.Client
	admin   *kadm.Client
	stop    context.CancelFunc
	done    chan error
}

func newKafkaFixture(t *testing.T, handler Handler, jobOptions JobOptions, configure func(*Options)) *kafkaFixture {
	t.Helper()

	brokers := strings.TrimSpace(os.Getenv("MILVAION_KAFKA_BROKERS"))

	if brokers == "" {
		t.Skip("set MILVAION_KAFKA_BROKERS (e.g. localhost:29092) to run the Kafka integration tests")
	}

	suffix := make([]byte, 4)
	_, _ = rand.Read(suffix)
	prefix := "it" + hex.EncodeToString(suffix) + "."
	workerID := "Go-Kafka-Worker"

	options := Options{
		WorkerID:             workerID,
		Transport:            "kafka",
		Kafka:                KafkaOptions{Brokers: []string{brokers}, TopicPrefix: prefix, NumPartitions: 2, ReplicationFactor: 1},
		DisableRedis:         true,
		MaxParallelJobs:      4,
		HeartbeatInterval:    time.Second,
		JobHeartbeatInterval: 150 * time.Millisecond,
		ShutdownTimeout:      5 * time.Second,
		StartupTimeout:       30 * time.Second,
		Logger:               slog.New(slog.NewTextHandler(io.Discard, nil)),
	}

	if testing.Verbose() {
		options.Logger = slog.New(slog.NewTextHandler(os.Stderr, &slog.HandlerOptions{Level: slog.LevelInfo}))
	}

	if configure != nil {
		configure(&options)
	}

	worker, err := NewWorker(options)

	if err != nil {
		t.Fatal(err)
	}

	if err := worker.Handle("TestJob", handler, jobOptions); err != nil {
		t.Fatal(err)
	}

	client, err := kgo.NewClient(kgo.SeedBrokers(strings.Split(brokers, ",")...))

	if err != nil {
		t.Fatal(err)
	}

	f := &kafkaFixture{
		t:       t,
		brokers: strings.Split(brokers, ","),
		prefix:  prefix,
		topics:  newKafkaTopics(prefix, workerID),
		worker:  worker,
		client:  client,
		admin:   kadm.NewClient(client),
		done:    make(chan error, 1),
	}

	ctx, stop := context.WithCancel(context.Background())
	f.stop = stop

	go func() { f.done <- worker.Run(ctx) }()

	t.Cleanup(func() {
		f.stopWorker()
		client.Close()
	})

	// Registration is produced after the topics have been created.
	f.waitFor(f.topics.registrations, 30*time.Second, func(records []*kgo.Record) bool {
		for _, r := range records {
			if string(r.Key) == worker.InstanceID() {
				return true
			}
		}

		return false
	})

	f.waitForGroupMember()

	return f
}

// waitForGroupMember waits until the worker owns its partitions, so a record produced next is read.
func (f *kafkaFixture) waitForGroupMember() {
	f.t.Helper()

	deadline := time.Now().Add(30 * time.Second)

	for time.Now().Before(deadline) {
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		groups, err := f.admin.DescribeGroups(ctx, f.topics.group)
		cancel()

		if err == nil {
			if g, ok := groups[f.topics.group]; ok && g.State == "Stable" && len(g.Members) > 0 {
				assigned := 0

				for _, m := range g.Members {
					if c, ok := m.Assigned.AsConsumer(); ok {
						for _, topic := range c.Topics {
							assigned += len(topic.Partitions)
						}
					}
				}

				if assigned == 4 { // 2 job + 2 retry partitions
					return
				}
			}
		}

		time.Sleep(100 * time.Millisecond)
	}

	f.t.Fatalf("worker did not join group %s", f.topics.group)
}

func (f *kafkaFixture) stopWorker() {
	if f.stop == nil {
		return
	}

	f.stop()
	f.stop = nil

	select {
	case err := <-f.done:
		if err != nil {
			f.t.Errorf("Run returned %v", err)
		}
	case <-time.After(60 * time.Second):
		f.t.Error("worker did not stop")
	}
}

func (f *kafkaFixture) produceJob(jobID, occurrenceID string, fields map[string]any) {
	f.t.Helper()

	body := map[string]any{"id": jobID, "displayName": "Kafka test", "jobNameInWorker": "TestJob", "jobData": `{"n":1}`, "workerId": "Go-Kafka-Worker"}

	for k, v := range fields {
		body[k] = v
	}

	value, _ := json.Marshal(body)

	f.produce(&kgo.Record{
		Topic: f.topics.jobs,
		Key:   []byte(jobID),
		Value: value,
		Headers: []kgo.RecordHeader{
			{Key: "OccurrenceId", Value: []byte(occurrenceID)},
			{Key: "CorrelationId", Value: []byte(occurrenceID)},
			{Key: "JobType", Value: []byte("TestJob")},
			{Key: "WorkerId", Value: []byte("Go-Kafka-Worker")},
			{Key: "Priority", Value: []byte("0")},
		},
	})
}

func (f *kafkaFixture) produce(record *kgo.Record) {
	f.t.Helper()

	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()

	if err := f.client.ProduceSync(ctx, record).FirstErr(); err != nil {
		f.t.Fatal(err)
	}
}

// waitFor reads a topic from the start until done accepts what was read, and returns the records.
func (f *kafkaFixture) waitFor(topic string, timeout time.Duration, done func([]*kgo.Record) bool) []*kgo.Record {
	f.t.Helper()

	reader, err := kgo.NewClient(kgo.SeedBrokers(f.brokers...), kgo.ConsumeTopics(topic), kgo.ConsumeResetOffset(kgo.NewOffset().AtStart()))

	if err != nil {
		f.t.Fatal(err)
	}

	defer reader.Close()

	var records []*kgo.Record
	deadline := time.Now().Add(timeout)

	for {
		if done(records) {
			return records
		}

		if time.Now().After(deadline) {
			f.t.Fatalf("timed out reading %s; got %d records: %s", topic, len(records), describeRecords(records))
		}

		ctx, cancel := context.WithTimeout(context.Background(), 250*time.Millisecond)
		records = append(records, reader.PollFetches(ctx).Records()...)
		cancel()
	}
}

func (f *kafkaFixture) statuses(occurrenceID string, until Status) []statusUpdateMessage {
	f.t.Helper()

	var out []statusUpdateMessage

	f.waitFor(f.topics.status, 30*time.Second, func(records []*kgo.Record) bool {
		out = out[:0]

		for _, r := range records {
			var s statusUpdateMessage

			if json.Unmarshal(r.Value, &s) == nil && s.OccurrenceID == occurrenceID {
				if string(r.Key) != occurrenceID {
					f.t.Fatalf("status record key = %q, want the occurrence id", r.Key)
				}

				out = append(out, s)
			}
		}

		return len(out) > 0 && out[len(out)-1].Status == until
	})

	return out
}

// committed sums the committed offsets of the worker's group on a topic.
func (f *kafkaFixture) committed(topic string) int64 {
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	offsets, err := f.admin.FetchOffsets(ctx, f.topics.group)

	if err != nil {
		return -1
	}

	var sum int64

	offsets.Each(func(o kadm.OffsetResponse) {
		if o.Topic == topic && o.Err == nil && o.At > 0 {
			sum += o.At
		}
	})

	return sum
}

func (f *kafkaFixture) expectCommitted(topic string, want int64) {
	f.t.Helper()

	deadline := time.Now().Add(10 * time.Second)

	for {
		got := f.committed(topic)

		if got == want {
			return
		}

		if time.Now().After(deadline) {
			f.t.Fatalf("committed offsets on %s = %d, want %d", topic, got, want)
		}

		time.Sleep(100 * time.Millisecond)
	}
}

func header(r *kgo.Record, key string) (string, bool) {
	for i := len(r.Headers) - 1; i >= 0; i-- {
		if r.Headers[i].Key == key {
			return string(r.Headers[i].Value), true
		}
	}

	return "", false
}

func describeRecords(records []*kgo.Record) string {
	var parts []string

	for _, r := range records {
		parts = append(parts, fmt.Sprintf("[%s/%d@%d key=%s %s]", r.Topic, r.Partition, r.Offset, r.Key, r.Value))
	}

	return strings.Join(parts, " ")
}

func newGUID() string {
	b := make([]byte, 16)
	_, _ = rand.Read(b)
	h := hex.EncodeToString(b)

	return h[0:8] + "-" + h[8:12] + "-" + h[12:16] + "-" + h[16:20] + "-" + h[20:32]
}

func statusList(statuses []statusUpdateMessage) []Status {
	out := make([]Status, len(statuses))

	for i, s := range statuses {
		out[i] = s.Status
	}

	return out
}

func TestKafkaCompletesJob(t *testing.T) {
	f := newKafkaFixture(t, func(_ context.Context, job *Job) (any, error) {
		job.Log("hello from kafka", nil)
		return map[string]any{"ok": true}, nil
	}, JobOptions{}, nil)

	jobID, occurrenceID := newGUID(), newGUID()
	f.produceJob(jobID, occurrenceID, nil)

	statuses := f.statuses(occurrenceID, StatusCompleted)

	if statuses[0].Status != StatusRunning || statuses[0].StartTime == nil {
		t.Fatalf("first status must be Running with a start time: %v", statusList(statuses))
	}

	final := statuses[len(statuses)-1]

	if final.JobID != jobID || final.Result == nil || *final.Result != `{"ok":true}` || final.InstanceID != f.worker.InstanceID() || final.DurationMs == nil {
		t.Fatalf("unexpected final status: %+v", final)
	}

	f.expectCommitted(f.topics.jobs, 1)

	f.waitFor(f.topics.logs, 10*time.Second, func(records []*kgo.Record) bool {
		for _, r := range records {
			if string(r.Key) == occurrenceID && strings.Contains(string(r.Value), "hello from kafka") {
				return true
			}
		}

		return false
	})

	f.waitFor(f.topics.heartbeats, 10*time.Second, func(records []*kgo.Record) bool {
		return len(records) > 0 && string(records[0].Key) == f.worker.InstanceID()
	})
}

func TestKafkaRetriesThenDeadLetters(t *testing.T) {
	var mu sync.Mutex
	var attempts []time.Time

	f := newKafkaFixture(t, func(_ context.Context, job *Job) (any, error) {
		mu.Lock()
		attempts = append(attempts, time.Now())
		mu.Unlock()

		return nil, errors.New("always fails")
	}, JobOptions{MaxRetries: 2, BaseRetryDelay: 400 * time.Millisecond}, nil)

	jobID, occurrenceID := newGUID(), newGUID()
	f.produceJob(jobID, occurrenceID, nil)

	dlq := f.waitFor(f.topics.dlq, 30*time.Second, func(records []*kgo.Record) bool { return len(records) > 0 })

	if len(dlq) != 1 || string(dlq[0].Key) != occurrenceID {
		t.Fatalf("dead letters: %s", describeRecords(dlq))
	}

	for key, want := range map[string]string{"OccurrenceId": occurrenceID, "CorrelationId": occurrenceID, "MaxRetries": "2", "x-retry-count": "2"} {
		if got, _ := header(dlq[0], key); got != want {
			t.Errorf("dead letter header %s = %q, want %q", key, got, want)
		}
	}

	var dead dlqMessage

	if err := json.Unmarshal(dlq[0].Value, &dead); err != nil || dead.ID != jobID || dead.Status != StatusFailed || !strings.HasPrefix(dead.Exception, "Max retries (2) exceeded.") {
		t.Fatalf("dead letter body: %s", dlq[0].Value)
	}

	retries := f.waitFor(f.topics.retry, 10*time.Second, func(records []*kgo.Record) bool { return len(records) == 2 })

	for i, r := range retries {
		count, _ := header(r, "x-retry-count")
		maxRetries, _ := header(r, "MaxRetries")
		occurrence, _ := header(r, "OccurrenceId")
		jobType, _ := header(r, "JobType")
		notBefore, ok := header(r, "x-not-before")
		ms, err := strconv.ParseInt(notBefore, 10, 64)

		if count != strconv.Itoa(i+1) || maxRetries != "2" || occurrence != occurrenceID || jobType != "TestJob" || !ok || err != nil || string(r.Key) != jobID {
			t.Fatalf("retry record %d: %s headers=%v", i, describeRecords([]*kgo.Record{r}), r.Headers)
		}

		// base * 2^retryCount of the failed attempt: 400ms, then 800ms.
		wantDelay := 400 * time.Millisecond << i

		if delay := time.UnixMilli(ms).Sub(r.Timestamp); delay < wantDelay-50*time.Millisecond || delay > wantDelay+500*time.Millisecond {
			t.Errorf("retry %d is due %s after it was written, want about %s", i+1, delay, wantDelay)
		}
	}

	mu.Lock()
	got := append([]time.Time(nil), attempts...)
	mu.Unlock()

	if len(got) != 3 {
		t.Fatalf("attempts = %d, want 3", len(got))
	}

	if gap := got[1].Sub(got[0]); gap < 400*time.Millisecond {
		t.Errorf("second attempt ran %s after the first; it must wait 400ms", gap)
	}

	if gap := got[2].Sub(got[1]); gap < 800*time.Millisecond {
		t.Errorf("third attempt ran %s after the second; it must wait 800ms", gap)
	}

	statuses := f.statuses(occurrenceID, StatusFailed)
	running := 0

	for _, s := range statuses[:len(statuses)-1] {
		if s.Status != StatusRunning {
			t.Fatalf("only Running may precede Failed: %v", statusList(statuses))
		}

		running++
	}

	// One per attempt, plus job heartbeats while the retries waited.
	if running <= 3 {
		t.Errorf("expected job heartbeats during the retry waits, got statuses %v", statusList(statuses))
	}

	f.expectCommitted(f.topics.jobs, 1)
	f.expectCommitted(f.topics.retry, 2)
}

func TestKafkaTimesOut(t *testing.T) {
	f := newKafkaFixture(t, func(ctx context.Context, _ *Job) (any, error) {
		<-ctx.Done()
		return nil, ctx.Err()
	}, JobOptions{}, nil)

	jobID, occurrenceID := newGUID(), newGUID()
	f.produceJob(jobID, occurrenceID, map[string]any{"executionTimeoutSeconds": 0.5})

	statuses := f.statuses(occurrenceID, StatusTimedOut)
	final := statuses[len(statuses)-1]

	if final.Exception == nil || !strings.Contains(*final.Exception, "exceeded maximum timeout of 0.5 seconds") {
		t.Fatalf("unexpected final status: %+v", final)
	}

	f.expectCommitted(f.topics.jobs, 1)
}

func TestKafkaForwardsPoisonRecordToDeadLetterTopic(t *testing.T) {
	f := newKafkaFixture(t, func(context.Context, *Job) (any, error) { return "ok", nil }, JobOptions{}, nil)

	f.produce(&kgo.Record{Topic: f.topics.jobs, Key: []byte("poison"), Value: []byte("not json"), Headers: []kgo.RecordHeader{{Key: "JobType", Value: []byte("TestJob")}}})

	dlq := f.waitFor(f.topics.dlq, 30*time.Second, func(records []*kgo.Record) bool { return len(records) > 0 })

	topic, _ := header(dlq[0], "x-original-topic")
	reason, _ := header(dlq[0], "x-dlq-reason")
	jobType, _ := header(dlq[0], "JobType")

	if string(dlq[0].Value) != "not json" || topic != f.topics.jobs || reason == "" || jobType != "TestJob" || string(dlq[0].Key) != "poison" {
		t.Fatalf("dead letter: %s headers=%v", describeRecords(dlq), dlq[0].Headers)
	}

	f.expectCommitted(f.topics.jobs, 1)
}

func TestKafkaShutdownWritesInterruptedJobBack(t *testing.T) {
	started := make(chan struct{}, 1)

	f := newKafkaFixture(t, func(ctx context.Context, _ *Job) (any, error) {
		started <- struct{}{}
		<-ctx.Done()
		return nil, ctx.Err()
	}, JobOptions{}, func(o *Options) { o.ShutdownTimeout = 300 * time.Millisecond })

	jobID, occurrenceID := newGUID(), newGUID()
	f.produceJob(jobID, occurrenceID, nil)

	select {
	case <-started:
	case <-time.After(30 * time.Second):
		t.Fatal("job did not start")
	}

	f.stopWorker()

	jobs := f.waitFor(f.topics.jobs, 10*time.Second, func(records []*kgo.Record) bool { return len(records) == 2 })
	requeued := jobs[1]

	if jobs[0].Offset > jobs[1].Offset && jobs[0].Partition == jobs[1].Partition {
		requeued = jobs[0]
	}

	if got, _ := header(requeued, "OccurrenceId"); got != occurrenceID || string(requeued.Key) != jobID {
		t.Fatalf("requeued record: %s", describeRecords([]*kgo.Record{requeued}))
	}

	if got := f.committed(f.topics.jobs); got != 1 {
		t.Fatalf("committed = %d, want 1 (the interrupted record, now written back)", got)
	}

	statuses := f.statuses(occurrenceID, StatusRunning)

	for _, s := range statuses {
		if s.Status != StatusRunning {
			t.Fatalf("an interrupted job must not report a final status: %v", statusList(statuses))
		}
	}
}

func TestKafkaRunsJobsOfOnePartitionInParallelUpToTheLimit(t *testing.T) {
	var mu sync.Mutex
	running, peak := 0, 0
	release := make(chan struct{})

	f := newKafkaFixture(t, func(context.Context, *Job) (any, error) {
		mu.Lock()
		running++
		peak = max(peak, running)
		mu.Unlock()

		<-release

		mu.Lock()
		running--
		mu.Unlock()

		return "ok", nil
	}, JobOptions{}, func(o *Options) { o.Kafka.MaxInFlightPerPartition = 2 })

	jobID := newGUID() // one key, so every record lands in the same partition
	occurrences := make([]string, 5)

	for i := range occurrences {
		occurrences[i] = newGUID()
		f.produceJob(jobID, occurrences[i], nil)
	}

	deadline := time.Now().Add(20 * time.Second)

	for {
		mu.Lock()
		current := running
		mu.Unlock()

		if current == 2 {
			break
		}

		if time.Now().After(deadline) {
			t.Fatalf("expected 2 jobs running from one partition, got %d", current)
		}

		time.Sleep(20 * time.Millisecond)
	}

	time.Sleep(500 * time.Millisecond) // a third job must not start meanwhile
	close(release)

	for _, occurrenceID := range occurrences {
		f.statuses(occurrenceID, StatusCompleted)
	}

	mu.Lock()
	defer mu.Unlock()

	if peak != 2 {
		t.Fatalf("peak parallel jobs of one partition = %d, want 2", peak)
	}

	f.expectCommitted(f.topics.jobs, 5)
}
