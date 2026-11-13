package milvaion

import (
	"context"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"regexp"
	"strings"
	"testing"
	"time"
)

func TestRoutingMatchesDotNetSDK(t *testing.T) {
	cases := map[[2]string]string{
		{"SendEmailJob", "Email-Worker-01"}: "email-worker-01.sendemail.*",
		{"NonParallel", "w"}:                "w.nonparallel.*",
	}

	for in, want := range cases {
		if got := GenerateRoutingPattern(in[0], in[1]); got != want {
			t.Errorf("GenerateRoutingPattern(%v) = %q, want %q", in, got, want)
		}
	}

	if got := QueueNameForPattern("email-worker-01.sendemail.*"); got != "scheduled_jobs_queue.email-worker-01.sendemail.wildcard" {
		t.Errorf("QueueNameForPattern = %q", got)
	}

	if got := QueueNameForPattern("a.#"); got != "scheduled_jobs_queue.a.all" {
		t.Errorf("QueueNameForPattern = %q", got)
	}

	if !regexp.MustCompile(`^go-worker-[0-9a-f]{8}$`).MatchString(generateInstanceID("go-worker")) {
		t.Error("instance id format")
	}
}

func TestParseOccurrenceID(t *testing.T) {
	id := "0193a6c2-5d1e-7c41-9a3b-2f6e8d4c1b00"

	cases := []struct {
		headers map[string]any
		corr    string
		want    string
	}{
		{map[string]any{"OccurrenceId": []byte(id)}, "", id},
		{map[string]any{"CorrelationId": id}, "", id},
		{nil, id, id},
		{map[string]any{"OccurrenceId": "not-a-guid"}, id, id},
		{nil, "", ""},
	}

	for _, c := range cases {
		if got := parseOccurrenceID(c.headers, c.corr); got != c.want {
			t.Errorf("parseOccurrenceID(%v, %q) = %q, want %q", c.headers, c.corr, got, c.want)
		}
	}
}

func TestParseRetryCount(t *testing.T) {
	cases := []struct {
		value any
		want  int
	}{
		{int8(2), 2}, {int16(3), 3}, {int32(4), 4}, {int64(5), 5}, {[]byte{6, 0, 0, 0}, 6}, {"7", 7}, {nil, 0},
	}

	for _, c := range cases {
		if got := parseRetryCount(map[string]any{"x-retry-count": c.value}); got != c.want {
			t.Errorf("parseRetryCount(%v) = %d, want %d", c.value, got, c.want)
		}
	}
}

func TestFormatErrorIncludesInnerErrors(t *testing.T) {
	text := formatError(fmt.Errorf("outer: %w", errors.New("inner")))

	if !strings.HasPrefix(text, "Type: *fmt.wrapError\nMessage: outer: inner\n") && !strings.HasPrefix(text, "Type: fmt.wrapError\nMessage: outer: inner\n") {
		t.Errorf("unexpected format: %q", text)
	}

	if !strings.Contains(text, "Inner Exception:\nType: ") || !strings.Contains(text, "Message: inner") {
		t.Errorf("inner error missing: %q", text)
	}

	if !IsPermanent(fmt.Errorf("wrapped: %w", Permanentf("x"))) {
		t.Error("IsPermanent must see wrapped permanent errors")
	}
}

func TestLogBufferKeepsOrderAfterFailedSend(t *testing.T) {
	var sent []string
	fail := true

	b := newLogBuffer(func(_ context.Context, batch workerLogBatchMessage) error {
		if fail {
			fail = false
			return errors.New("broker down")
		}

		for _, l := range batch.Logs {
			sent = append(sent, l.Log.Message)
		}

		return nil
	}, slog.New(slog.NewTextHandler(io.Discard, nil)))
	b.batchSize, b.flushInterval = 1000, time.Hour
	defer b.close()

	b.add("o", "i", occurrenceLog{Message: "1"})
	b.add("o", "i", occurrenceLog{Message: "2"})

	if err := b.flush(context.Background()); err == nil {
		t.Fatal("expected the first flush to fail")
	}

	b.add("o", "i", occurrenceLog{Message: "3"})

	if err := b.flush(context.Background()); err != nil {
		t.Fatal(err)
	}

	if strings.Join(sent, ",") != "1,2,3" {
		t.Fatalf("sent = %v", sent)
	}
}

func TestLogBufferDropsOldestBeyondLimit(t *testing.T) {
	var sent []string

	b := newLogBuffer(func(_ context.Context, batch workerLogBatchMessage) error {
		for _, l := range batch.Logs {
			sent = append(sent, l.Log.Message)
		}

		return nil
	}, slog.New(slog.NewTextHandler(io.Discard, nil)))
	b.batchSize, b.flushInterval, b.maxBuffered = 1000, time.Hour, 2
	defer b.close()

	for _, m := range []string{"1", "2", "3"} {
		b.add("o", "i", occurrenceLog{Message: m})
	}

	_ = b.flush(context.Background())

	if strings.Join(sent, ",") != "2,3" {
		t.Fatalf("sent = %v", sent)
	}
}

func TestOptionsDefaults(t *testing.T) {
	o, err := resolveOptions(Options{WorkerID: " report-worker "})

	if err != nil {
		t.Fatal(err)
	}

	if o.workerID != "report-worker" || o.rabbitURL != "amqp://guest:guest@localhost:5672/" || o.quorum ||
		o.cancellationChannel != "Milvaion:JobScheduler:cancellation_channel" || o.executionTimeout != time.Hour || o.redisURL != "redis://localhost:6379" {
		t.Fatalf("unexpected defaults: %+v", o)
	}

	job, err := resolveJob("SendEmailJob", func(context.Context, *Job) (any, error) { return nil, nil }, JobOptions{}, o)

	if err != nil {
		t.Fatal(err)
	}

	if job.routingPattern != "report-worker.sendemail.*" || job.consumerID != "sendemail-consumer" || job.maxRetries != 3 || job.baseRetryDelay != 5*time.Second {
		t.Fatalf("unexpected job defaults: %+v", job)
	}

	if _, err := resolveOptions(Options{}); err == nil {
		t.Error("missing WorkerID must fail")
	}

	if _, err := resolveOptions(Options{WorkerID: "w", QueueType: "lazy"}); err == nil {
		t.Error("unknown queue type must fail")
	}

	noRetry, _ := resolveJob("J", func(context.Context, *Job) (any, error) { return nil, nil }, JobOptions{MaxRetries: NoRetries}, o)

	if noRetry.maxRetries != 0 {
		t.Error("NoRetries must disable retries")
	}
}

func TestOptionsFromEnv(t *testing.T) {
	t.Setenv("MILVAION_WORKER_ID", "env-worker")
	t.Setenv("MILVAION_RABBITMQ_URL", "amqp://u:p@rabbit:5672/")
	t.Setenv("MILVAION_RABBITMQ_QUEUE_TYPE", "Quorum")
	t.Setenv("MILVAION_REDIS_URL", "redis://redis:6379")
	t.Setenv("MILVAION_REDIS_KEY_PREFIX", "X:")
	t.Setenv("MILVAION_MAX_PARALLEL_JOBS", "4")

	o, err := OptionsFromEnv(Options{})

	if err != nil {
		t.Fatal(err)
	}

	resolved, err := resolveOptions(o)

	if err != nil {
		t.Fatal(err)
	}

	if resolved.workerID != "env-worker" || !resolved.quorum || resolved.maxParallelJobs != 4 || resolved.cancellationChannel != "X:cancellation_channel" {
		t.Fatalf("unexpected options: %+v", resolved)
	}

	t.Setenv("MILVAION_REDIS_URL", "false")

	if o, _ := OptionsFromEnv(Options{}); !o.DisableRedis {
		t.Error("MILVAION_REDIS_URL=false must disable Redis")
	}
}

func TestHandleRejectsDuplicates(t *testing.T) {
	w, err := NewWorker(Options{WorkerID: "w", DisableRedis: true})

	if err != nil {
		t.Fatal(err)
	}

	h := func(context.Context, *Job) (any, error) { return nil, nil }

	if err := w.Handle("TestJob", h); err != nil {
		t.Fatal(err)
	}

	if err := w.Handle("testjob", h); err == nil {
		t.Error("duplicate job names must be rejected")
	}
}

func TestRegistrationMetadataIsPascalCase(t *testing.T) {
	w, _ := NewWorker(Options{WorkerID: "w", DisableRedis: true})
	_ = w.Handle("GreetJob", func(context.Context, *Job) (any, error) { return nil, nil }, JobOptions{DataSchema: map[string]any{"type": "object"}})

	r := w.buildRegistration()

	if !strings.Contains(r.Metadata, `"Sdk":"go","SdkVersion":"`+SDKVersion+`"`) {
		t.Errorf("metadata does not name the SDK: %s", r.Metadata)
	}

	if !strings.Contains(r.Metadata, `"JobConfigs":[{"JobType":"GreetJob","ConsumerId":"greet-consumer"`) {
		t.Errorf("metadata = %s", r.Metadata)
	}

	if r.JobDataDefinitions["GreetJob"] != `{"type":"object"}` || r.RoutingPatterns["GreetJob"] != "w.greet.*" {
		t.Errorf("registration = %+v", r)
	}
}
