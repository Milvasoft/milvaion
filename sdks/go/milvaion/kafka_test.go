package milvaion

import (
	"context"
	"errors"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/twmb/franz-go/pkg/kgo"
)

func TestKafkaWorkerSegment(t *testing.T) {
	cases := map[string]string{
		"report-worker":     "report-worker",
		"Report_Worker.v2":  "report_worker.v2",
		"Mail Worker":       "mail_worker",
		"a/b:c*d":           "a_b_c_d",
		"ÇiçekWorker":       "_i_ekworker", // one "_" per character, not per byte
		"UPPER-123":         "upper-123",
		"emoji😀worker":      "emoji_worker",
		"tab\tand\nnewline": "tab_and_newline",
	}

	for in, want := range cases {
		if got := kafkaWorkerSegment(in); got != want {
			t.Errorf("kafkaWorkerSegment(%q) = %q, want %q", in, got, want)
		}
	}
}

func TestKafkaTopicNames(t *testing.T) {
	topics := newKafkaTopics("prod.", "Report Worker")

	want := kafkaTopics{
		jobs:          "prod.milvaion.jobs.report_worker",
		retry:         "prod.milvaion.jobs.report_worker.retry",
		status:        "prod.milvaion.status",
		logs:          "prod.milvaion.logs",
		heartbeats:    "prod.milvaion.heartbeats",
		registrations: "prod.milvaion.registrations",
		dlq:           "prod.milvaion.dlq",
		group:         "prod.milvaion.worker.report_worker",
	}

	if topics != want {
		t.Fatalf("topics = %+v, want %+v", topics, want)
	}

	if plain := newKafkaTopics("", "w"); plain.jobs != "milvaion.jobs.w" || plain.group != "milvaion.worker.w" {
		t.Fatalf("unprefixed topics = %+v", plain)
	}
}

func TestKafkaHeaders(t *testing.T) {
	headers := kafkaHeaderMap([]kgo.RecordHeader{
		{Key: "CorrelationId", Value: []byte(testOccurrenceID)},
		{Key: "x-retry-count", Value: []byte("2")},
		{Key: "x-not-before", Value: []byte("1794612341949")},
	})

	if got := parseOccurrenceID(headers, ""); got != testOccurrenceID {
		t.Errorf("occurrence id from CorrelationId = %q", got)
	}

	headers["OccurrenceId"] = "0193a6c2-5d1e-7c41-9a3b-2f6e8d4c1b01"

	if got := parseOccurrenceID(headers, ""); got != "0193a6c2-5d1e-7c41-9a3b-2f6e8d4c1b01" {
		t.Errorf("OccurrenceId must win over CorrelationId, got %q", got)
	}

	if got := parseRetryCount(headers); got != 2 {
		t.Errorf("retry count = %d", got)
	}

	if got := parseRetryCount(kafkaHeaderMap(nil)); got != 0 {
		t.Errorf("missing retry count = %d", got)
	}

	due, ok := kafkaNotBefore(headers)

	if !ok || due.UnixMilli() != 1794612341949 {
		t.Errorf("not before = %v, %v", due, ok)
	}

	if _, ok := kafkaNotBefore(map[string]any{"x-not-before": "soon"}); ok {
		t.Error("an invalid x-not-before must be ignored")
	}

	encoded := kafkaRecordHeaders(map[string]any{"x-retry-count": int32(3), "MaxRetries": 0, "CorrelationId": []byte("abc"), "Priority": int64(9)})
	got := map[string]string{}

	for i, h := range encoded {
		got[h.Key] = string(h.Value)

		if i > 0 && encoded[i-1].Key > h.Key {
			t.Error("headers must be in a stable order")
		}
	}

	if got["x-retry-count"] != "3" || got["MaxRetries"] != "0" || got["CorrelationId"] != "abc" || got["Priority"] != "9" {
		t.Errorf("encoded headers = %v", got)
	}
}

func TestOffsetTrackerCommitsContiguousRange(t *testing.T) {
	tr := newOffsetTracker()

	if _, ok := tr.commitPosition(); ok {
		t.Fatal("nothing is committable before a record finishes")
	}

	for _, o := range []int64{10, 11, 12, 14} { // 13 is a gap, for example a transaction marker
		if !tr.add(o) {
			t.Fatalf("add(%d) refused", o)
		}
	}

	if tr.add(12) || tr.add(5) {
		t.Fatal("repeated or older offsets must be ignored")
	}

	tr.done(11)

	if _, ok := tr.commitPosition(); ok {
		t.Fatal("11 finished before 10: nothing may be committed yet")
	}

	tr.done(10)

	if pos, _ := tr.commitPosition(); pos != 12 {
		t.Fatalf("position = %d, want 12", pos)
	}

	tr.done(14)

	if pos, _ := tr.commitPosition(); pos != 12 || tr.outstanding() != 2 {
		t.Fatalf("position = %d, outstanding = %d; 12 still runs", pos, tr.outstanding())
	}

	tr.done(12)

	if pos, _ := tr.commitPosition(); pos != 15 || tr.outstanding() != 0 {
		t.Fatalf("position = %d, want 15 across the gap", pos)
	}

	if tr.done(99) {
		t.Fatal("done of an unknown offset must report false")
	}

	tr.add(20)

	if pos, _ := tr.commitPosition(); pos != 15 {
		t.Fatalf("a new unfinished record must not move the position, got %d", pos)
	}
}

func TestKafkaOptions(t *testing.T) {
	o, err := resolveOptions(Options{WorkerID: "w", Transport: "Kafka", MaxParallelJobs: 6})

	if err != nil {
		t.Fatal(err)
	}

	k := o.kafka

	if o.transport != transportKafka || len(k.brokers) != 1 || k.brokers[0] != "localhost:9092" || k.topicPrefix != "" || !k.autoCreateTopics ||
		k.numPartitions != 12 || k.replicationFactor != -1 || k.maxInFlightPerPartition != 6 || k.tls != nil || k.sasl != nil {
		t.Fatalf("unexpected Kafka defaults: %+v", k)
	}

	o, err = resolveOptions(Options{WorkerID: "w", Transport: "kafka", Kafka: KafkaOptions{
		Brokers: []string{"a:9092, b:9092", "c:9092"}, TLS: true, SASLMechanism: "SCRAM-SHA-512", SASLUsername: "u", SASLPassword: "p",
		TopicPrefix: "prod.", DisableAutoCreateTopics: true, NumPartitions: 3, ReplicationFactor: 2, MaxInFlightPerPartition: 1,
	}})

	if err != nil {
		t.Fatal(err)
	}

	k = o.kafka

	if strings.Join(k.brokers, "|") != "a:9092|b:9092|c:9092" || k.tls == nil || k.sasl == nil || k.sasl.Name() != "SCRAM-SHA-512" ||
		k.autoCreateTopics || k.numPartitions != 3 || k.replicationFactor != 2 || k.maxInFlightPerPartition != 1 {
		t.Fatalf("unexpected Kafka options: %+v", k)
	}

	if def, _ := resolveOptions(Options{WorkerID: "w"}); def.transport != transportRabbitMQ {
		t.Errorf("default transport = %q", def.transport)
	}

	invalid := []Options{
		{WorkerID: "w", Transport: "nats"},
		{WorkerID: "w", Transport: "kafka", Kafka: KafkaOptions{TopicPrefix: "bad prefix"}},
		{WorkerID: "w", Transport: "kafka", Kafka: KafkaOptions{SASLMechanism: "gssapi", SASLUsername: "u"}},
		{WorkerID: "w", Transport: "kafka", Kafka: KafkaOptions{SASLMechanism: "plain"}},
		{WorkerID: "w", Transport: "kafka", Kafka: KafkaOptions{ReplicationFactor: -2}},
		{WorkerID: "w", Transport: "kafka", Kafka: KafkaOptions{TLSCertFile: "only-cert.pem"}},
		{WorkerID: "w", Transport: "kafka", Kafka: KafkaOptions{TLSCAFile: "does-not-exist.pem"}},
	}

	for _, options := range invalid {
		if _, err := resolveOptions(options); err == nil {
			t.Errorf("expected an error for %+v", options)
		}
	}
}

func TestKafkaOptionsFromEnv(t *testing.T) {
	t.Setenv("MILVAION_TRANSPORT", "kafka")
	t.Setenv("KAFKA_BOOTSTRAP_SERVERS", "k1:9092,k2:9092")
	t.Setenv("MILVAION_KAFKA_TOPIC_PREFIX", "stage.")
	t.Setenv("MILVAION_KAFKA_SASL_MECHANISM", "plain")
	t.Setenv("MILVAION_KAFKA_SASL_USERNAME", "user")
	t.Setenv("MILVAION_KAFKA_SASL_PASSWORD", "secret")
	t.Setenv("MILVAION_KAFKA_AUTO_CREATE_TOPICS", "false")
	t.Setenv("MILVAION_KAFKA_PARTITIONS", "6")
	t.Setenv("MILVAION_KAFKA_REPLICATION_FACTOR", "3")
	t.Setenv("MILVAION_KAFKA_MAX_IN_FLIGHT_PER_PARTITION", "2")

	o, err := OptionsFromEnv(Options{WorkerID: "w"})

	if err != nil {
		t.Fatal(err)
	}

	resolved, err := resolveOptions(o)

	if err != nil {
		t.Fatal(err)
	}

	k := resolved.kafka

	if resolved.transport != transportKafka || strings.Join(k.brokers, ",") != "k1:9092,k2:9092" || k.topicPrefix != "stage." || k.sasl.Name() != "PLAIN" ||
		k.autoCreateTopics || k.numPartitions != 6 || k.replicationFactor != 3 || k.maxInFlightPerPartition != 2 {
		t.Fatalf("unexpected options: %+v", k)
	}

	t.Setenv("MILVAION_KAFKA_BOOTSTRAP_SERVERS", "preferred:9092")

	if o, _ := OptionsFromEnv(Options{}); o.Kafka.Brokers[0] != "preferred:9092" {
		t.Errorf("MILVAION_KAFKA_BOOTSTRAP_SERVERS must win, got %v", o.Kafka.Brokers)
	}
}

// The Kafka transport plugs into the processor through delivery.scheduleRetry and delivery.reject.

func TestScheduledRetryIsHandedToTheBroker(t *testing.T) {
	h := newHarness(t, map[string]Handler{"TestJob": func(context.Context, *Job) (any, error) { return nil, errors.New("boom") }},
		JobOptions{MaxRetries: 3, BaseRetryDelay: 5 * time.Second})
	d, f := newDelivery(nil, map[string]any{"x-retry-count": "1"}, false)

	var mu sync.Mutex
	var requests []retryRequest

	d.scheduleRetry = func(_ context.Context, r retryRequest) error {
		mu.Lock()
		requests = append(requests, r)
		mu.Unlock()
		return nil
	}

	before := time.Now()
	h.proc.handle(d, h.consumer)

	expectSettled(t, f, "ack")
	expectStatuses(t, h, StatusRunning)

	if len(h.sleeps) != 0 {
		t.Fatal("a broker-scheduled retry must not wait in process")
	}

	if len(requests) != 1 {
		t.Fatalf("retry requests = %v", requests)
	}

	r := requests[0]
	wait := r.notBefore.Sub(before)

	if r.retryCount != 2 || r.maxRetries != 3 || r.occurrenceID != testOccurrenceID || r.jobID != testJobID || wait < 10*time.Second || wait > 11*time.Second {
		t.Fatalf("unexpected retry request: %+v (wait %s)", r, wait)
	}

	if len(h.pub.to(h.consumer.queue)) != 0 || len(h.pub.deadLettered()) != 0 {
		t.Fatal("nothing may be republished or dead-lettered")
	}
}

func TestRetryAfterIsNotHandedToTheBroker(t *testing.T) {
	at := time.Now().Add(time.Hour)
	h := newHarness(t, map[string]Handler{"TestJob": func(context.Context, *Job) (any, error) { return nil, RetryAfter(at, "quota") }},
		JobOptions{MaxRetries: 3, BaseRetryDelay: 5 * time.Second})
	d, f := newDelivery(nil, nil, false)

	scheduled := 0
	d.scheduleRetry = func(context.Context, retryRequest) error { scheduled++; return nil }
	d.reject = func(string) error { t.Error("a retry-after failure must not be rejected"); return nil }

	h.proc.handle(d, h.consumer)

	expectSettled(t, f, "ack")
	expectStatuses(t, h, StatusRunning, StatusFailed)

	if scheduled != 0 || len(h.sleeps) != 0 {
		t.Fatal("a retry-after failure must not be retried by the worker")
	}

	if len(h.pub.to(h.consumer.queue)) != 0 || len(h.pub.deadLettered()) != 0 {
		t.Fatal("nothing may be republished or dead-lettered")
	}

	if h.pub.lastStatus()["retryAfter"] != at.UTC().Format(time.RFC3339Nano) {
		t.Fatalf("retryAfter = %v", h.pub.lastStatus()["retryAfter"])
	}
}

func TestFailedScheduledRetryIsRejected(t *testing.T) {
	h := newHarness(t, map[string]Handler{"TestJob": func(context.Context, *Job) (any, error) { return nil, errors.New("boom") }}, JobOptions{})
	d, f := newDelivery(nil, nil, false)

	var reasons []string

	d.scheduleRetry = func(context.Context, retryRequest) error { return errors.New("broker down") }
	d.reject = func(reason string) error { reasons = append(reasons, reason); return nil }

	h.proc.handle(d, h.consumer)

	if len(f.get()) != 0 || len(reasons) != 1 || !strings.Contains(reasons[0], "broker down") {
		t.Fatalf("settled = %v, reasons = %v", f.get(), reasons)
	}
}

func TestRejectCarriesTheReason(t *testing.T) {
	h := newHarness(t, map[string]Handler{"TestJob": func(context.Context, *Job) (any, error) { return "ok", nil }}, JobOptions{})
	d, f := newDelivery(nil, nil, false)
	d.body = []byte("not json")

	var reasons []string
	d.reject = func(reason string) error { reasons = append(reasons, reason); return nil }

	h.proc.handle(d, h.consumer)

	if len(f.get()) != 0 || len(reasons) != 1 || reasons[0] != "the message is not a valid job" {
		t.Fatalf("settled = %v, reasons = %v", f.get(), reasons)
	}
}

func TestStatusAndDeadLetterCarryTheRecordKey(t *testing.T) {
	h := newHarness(t, map[string]Handler{"TestJob": func(context.Context, *Job) (any, error) { return nil, Permanentf("bad") }}, JobOptions{})
	d, _ := newDelivery(nil, nil, false)

	h.proc.handle(d, h.consumer)

	for _, m := range h.pub.to(queueStatusUpdates) {
		if m.options.key != testOccurrenceID {
			t.Fatalf("status key = %q", m.options.key)
		}
	}

	if dlq := h.pub.deadLettered(); len(dlq) != 1 || dlq[0].options.key != testOccurrenceID {
		t.Fatalf("dead letter = %+v", dlq)
	}
}
