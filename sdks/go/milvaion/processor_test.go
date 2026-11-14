package milvaion

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"log/slog"
	"strings"
	"sync"
	"testing"
	"time"
)

const (
	testOccurrenceID = "0193a6c2-5d1e-7c41-9a3b-2f6e8d4c1b00"
	testJobID        = "0193a6c2-0000-7c41-9a3b-2f6e8d4c1b99"
)

type published struct {
	exchange, key string
	body          map[string]any
	raw           []byte
	options       publishOptions
}

type fakePublisher struct {
	mu       sync.Mutex
	messages []published
	failNext int
}

func (f *fakePublisher) publish(_ context.Context, exchange, key string, body []byte, o publishOptions) error {
	f.mu.Lock()
	defer f.mu.Unlock()

	if f.failNext > 0 {
		f.failNext--
		return errors.New("publish failed")
	}

	var parsed map[string]any
	_ = json.Unmarshal(body, &parsed)
	f.messages = append(f.messages, published{exchange: exchange, key: key, body: parsed, raw: body, options: o})

	return nil
}

func (f *fakePublisher) to(queue string) []published {
	f.mu.Lock()
	defer f.mu.Unlock()

	var out []published

	for _, m := range f.messages {
		if m.exchange == "" && m.key == queue {
			out = append(out, m)
		}
	}

	return out
}

func (f *fakePublisher) statuses() []int {
	var out []int

	for _, m := range f.to(queueStatusUpdates) {
		out = append(out, int(m.body["status"].(float64)))
	}

	return out
}

func (f *fakePublisher) lastStatus() map[string]any {
	all := f.to(queueStatusUpdates)

	return all[len(all)-1].body
}

func (f *fakePublisher) logs() []map[string]any {
	var out []map[string]any

	for _, m := range f.to(queueWorkerLogs) {
		for _, entry := range m.body["logs"].([]any) {
			out = append(out, entry.(map[string]any)["log"].(map[string]any))
		}
	}

	return out
}

func (f *fakePublisher) deadLettered() []published {
	f.mu.Lock()
	defer f.mu.Unlock()

	var out []published

	for _, m := range f.messages {
		if m.exchange == exchangeDeadLetter {
			out = append(out, m)
		}
	}

	return out
}

type fakeDelivery struct {
	mu      sync.Mutex
	settled []string
}

func (f *fakeDelivery) get() []string {
	f.mu.Lock()
	defer f.mu.Unlock()

	return append([]string(nil), f.settled...)
}

func newDelivery(job map[string]any, headers map[string]any, redelivered bool) (delivery, *fakeDelivery) {
	body := map[string]any{"id": testJobID, "displayName": "Test", "jobNameInWorker": "TestJob", "jobData": nil}

	for k, v := range job {
		body[k] = v
	}

	raw, _ := json.Marshal(body)
	h := map[string]any{"OccurrenceId": testOccurrenceID}

	for k, v := range headers {
		h[k] = v
	}

	f := &fakeDelivery{}

	return delivery{
		body:        raw,
		headers:     h,
		redelivered: redelivered,
		ack:         func() error { f.mu.Lock(); f.settled = append(f.settled, "ack"); f.mu.Unlock(); return nil },
		nack: func(requeue bool) error {
			f.mu.Lock()
			defer f.mu.Unlock()

			if requeue {
				f.settled = append(f.settled, "requeue")
			} else {
				f.settled = append(f.settled, "nack")
			}

			return nil
		},
	}, f
}

type harness struct {
	proc     *processor
	pub      *fakePublisher
	cancels  *cancelRegistry
	consumer *resolvedJob
	sleeps   []time.Duration
}

func newHarness(t *testing.T, handlers map[string]Handler, jobOptions JobOptions) *harness {
	t.Helper()

	opts, err := resolveOptions(Options{
		WorkerID:             "go-worker",
		DisableRedis:         true,
		JobHeartbeatInterval: -1,
		InstanceID:           "go-worker-abcd1234",
		Logger:               slog.New(slog.NewTextHandler(io.Discard, nil)),
	})

	if err != nil {
		t.Fatal(err)
	}

	pub := &fakePublisher{}
	logs := newLogBuffer(func(ctx context.Context, batch workerLogBatchMessage) error {
		body, _ := json.Marshal(batch)
		return pub.publish(ctx, "", queueWorkerLogs, body, publishOptions{persistent: true})
	}, opts.logger)
	logs.flushInterval = 5 * time.Millisecond
	jobs := map[string]*resolvedJob{}
	h := &harness{pub: pub, cancels: newCancelRegistry()}

	for name, handler := range handlers {
		job, err := resolveJob(name, handler, jobOptions, opts)

		if err != nil {
			t.Fatal(err)
		}

		jobs[strings.ToLower(name)] = &job
		h.consumer = &job
	}

	h.proc = newProcessor(opts, jobs, pub, logs, h.cancels)
	h.proc.sleep = func(ctx context.Context, d time.Duration) error {
		h.sleeps = append(h.sleeps, d)
		return ctx.Err()
	}

	t.Cleanup(logs.close)

	return h
}

func equalInts(a, b []int) bool {
	if len(a) != len(b) {
		return false
	}

	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}

	return true
}

func expectStatuses(t *testing.T, h *harness, want ...Status) {
	t.Helper()

	wantInts := make([]int, len(want))

	for i, s := range want {
		wantInts[i] = int(s)
	}

	if got := h.pub.statuses(); !equalInts(got, wantInts) {
		t.Fatalf("statuses = %v, want %v", got, wantInts)
	}
}

func expectSettled(t *testing.T, f *fakeDelivery, want string) {
	t.Helper()

	if got := f.get(); len(got) != 1 || got[0] != want {
		t.Fatalf("settled = %v, want [%s]", got, want)
	}
}

func TestCompletesAndReportsResult(t *testing.T) {
	h := newHarness(t, map[string]Handler{
		"TestJob": func(ctx context.Context, job *Job) (any, error) {
			var data struct{ To string }

			if err := job.Bind(&data); err != nil {
				return nil, err
			}

			job.Log("working", map[string]any{"to": data.To})

			return map[string]bool{"sent": true}, nil
		},
	}, JobOptions{})

	d, f := newDelivery(map[string]any{"jobData": `{"to":"a@b.c"}`}, nil, false)
	h.proc.handle(d, h.consumer)

	expectSettled(t, f, "ack")
	expectStatuses(t, h, StatusRunning, StatusCompleted)

	final := h.pub.lastStatus()

	if final["occurrenceId"] != testOccurrenceID || final["jobId"] != testJobID || final["workerId"] != "go-worker" || final["instanceId"] != "go-worker-abcd1234" {
		t.Fatalf("unexpected identity fields: %v", final)
	}

	if final["result"] != `{"sent":true}` || final["startTime"] == nil || final["endTime"] == nil || final["durationMs"] == nil {
		t.Fatalf("unexpected final status: %v", final)
	}

	found := false

	for _, l := range h.pub.logs() {
		if l["message"] == "working" && l["level"] == "Information" && l["category"] == "UserCode" && l["data"].(map[string]any)["to"] == "a@b.c" {
			found = true
		}
	}

	if !found {
		t.Fatalf("user log missing: %v", h.pub.logs())
	}
}

func TestDefaultResultMessage(t *testing.T) {
	h := newHarness(t, map[string]Handler{"TestJob": func(context.Context, *Job) (any, error) { return nil, nil }}, JobOptions{})
	d, _ := newDelivery(nil, nil, false)
	h.proc.handle(d, h.consumer)

	if got := h.pub.lastStatus()["result"]; got != "Job TestJob completed successfully" {
		t.Fatalf("result = %v", got)
	}
}

func TestRetriesWithBackoff(t *testing.T) {
	h := newHarness(t, map[string]Handler{"TestJob": func(context.Context, *Job) (any, error) { return nil, errors.New("boom") }},
		JobOptions{MaxRetries: 3, BaseRetryDelay: 5 * time.Second})
	d, f := newDelivery(nil, map[string]any{"x-retry-count": int32(1)}, false)

	h.proc.handle(d, h.consumer)

	expectSettled(t, f, "ack")
	expectStatuses(t, h, StatusRunning)

	if len(h.sleeps) != 1 || h.sleeps[0] != 10*time.Second {
		t.Fatalf("sleeps = %v, want [10s]", h.sleeps)
	}

	retries := h.pub.to(h.consumer.queue)

	if len(retries) != 1 {
		t.Fatalf("expected one republished message, got %d", len(retries))
	}

	headers := retries[0].options.headers

	if headers["x-retry-count"] != int32(2) || headers["MaxRetries"] != int32(3) || headers["CorrelationId"] != testOccurrenceID {
		t.Fatalf("unexpected retry headers: %v", headers)
	}

	if string(retries[0].raw) != string(d.body) {
		t.Fatal("retry must republish the original body")
	}

	if len(h.pub.deadLettered()) != 0 {
		t.Fatal("a retried job must not be dead-lettered")
	}
}

func TestDeadLettersAfterRetries(t *testing.T) {
	h := newHarness(t, map[string]Handler{"TestJob": func(context.Context, *Job) (any, error) { return nil, errors.New("still broken") }}, JobOptions{MaxRetries: 3})
	d, f := newDelivery(nil, map[string]any{"x-retry-count": int32(3)}, false)

	h.proc.handle(d, h.consumer)

	expectSettled(t, f, "ack")
	expectStatuses(t, h, StatusRunning, StatusFailed)

	if exception := h.pub.lastStatus()["exception"].(string); !strings.HasPrefix(exception, "Max retries (3) exceeded. Original exception: Type: ") || !strings.Contains(exception, "Message: still broken") {
		t.Fatalf("exception = %q", exception)
	}

	dlq := h.pub.deadLettered()

	if len(dlq) != 1 || dlq[0].key != deadLetterRoutingKey || dlq[0].body["id"] != testJobID || dlq[0].body["status"] != float64(StatusFailed) {
		t.Fatalf("unexpected DLQ message: %+v", dlq)
	}

	if dlq[0].options.headers["x-retry-count"] != int32(3) || dlq[0].options.headers["MaxRetries"] != int32(3) {
		t.Fatalf("unexpected DLQ headers: %v", dlq[0].options.headers)
	}
}

func TestPermanentFailureSkipsRetries(t *testing.T) {
	h := newHarness(t, map[string]Handler{"TestJob": func(context.Context, *Job) (any, error) { return nil, Permanentf("bad input") }}, JobOptions{})
	d, f := newDelivery(nil, nil, false)

	h.proc.handle(d, h.consumer)

	expectSettled(t, f, "ack")
	expectStatuses(t, h, StatusRunning, StatusFailed)

	if len(h.sleeps) != 0 {
		t.Fatal("a permanent failure must not wait for a retry")
	}

	dlq := h.pub.deadLettered()

	if len(dlq) != 1 || !strings.HasPrefix(dlq[0].body["exception"].(string), "[PERMANENT FAILURE] Type: ") || dlq[0].options.headers["MaxRetries"] != int32(0) {
		t.Fatalf("unexpected DLQ message: %+v", dlq)
	}
}

func TestRetryAfterReportsFailedWithTheRequestedTime(t *testing.T) {
	at := time.Date(2026, 11, 14, 11, 0, 0, 0, time.FixedZone("UTC+3", 3*3600))
	h := newHarness(t, map[string]Handler{"TestJob": func(context.Context, *Job) (any, error) {
		return nil, RetryAfter(at, "YouTube quota exhausted")
	}}, JobOptions{MaxRetries: 3})
	d, f := newDelivery(nil, nil, false)

	h.proc.handle(d, h.consumer)

	expectSettled(t, f, "ack")
	expectStatuses(t, h, StatusRunning, StatusFailed)

	if len(h.sleeps) != 0 {
		t.Fatal("a retry-after failure must not wait for a local retry")
	}

	if len(h.pub.to(h.consumer.queue)) != 0 || len(h.pub.deadLettered()) != 0 {
		t.Fatal("a retry-after failure must not be republished or dead-lettered")
	}

	last := h.pub.lastStatus()

	if last["retryAfter"] != "2026-11-14T08:00:00Z" {
		t.Fatalf("retryAfter = %v", last["retryAfter"])
	}

	if exception, _ := last["exception"].(string); !strings.HasPrefix(exception, "Type: ") || !strings.Contains(exception, "YouTube quota exhausted (retry at 2026-11-14T08:00:00Z)") {
		t.Fatalf("exception = %q", exception)
	}

	if last["startTime"] == nil || last["endTime"] == nil || last["durationMs"] == nil {
		t.Fatalf("timing missing: %v", last)
	}

	for _, l := range h.pub.logs() {
		if l["category"] == "Retry" && l["level"] == string(LevelWarning) && l["data"].(map[string]any)["RetryAfter"] == "2026-11-14T08:00:00Z" &&
			strings.HasPrefix(l["message"].(string), "⏰ Job asked to be retried at 2026-11-14T08:00:00Z.") {
			return
		}
	}

	t.Fatalf("retry-after log missing: %v", h.pub.logs())
}

func TestOtherFailuresOmitRetryAfter(t *testing.T) {
	h := newHarness(t, map[string]Handler{"TestJob": func(context.Context, *Job) (any, error) { return nil, Permanentf("bad") }}, JobOptions{})
	d, _ := newDelivery(nil, nil, false)

	h.proc.handle(d, h.consumer)

	if _, ok := h.pub.lastStatus()["retryAfter"]; ok {
		t.Fatal("retryAfter must only be sent for RetryAfterError")
	}
}

func TestInvalidJobDataIsPermanent(t *testing.T) {
	h := newHarness(t, map[string]Handler{"TestJob": func(_ context.Context, job *Job) (any, error) {
		var data struct{ Name string }
		return nil, job.Bind(&data)
	}}, JobOptions{})
	d, _ := newDelivery(map[string]any{"jobData": "{not json"}, nil, false)

	h.proc.handle(d, h.consumer)

	if dlq := h.pub.deadLettered(); len(dlq) != 1 || !strings.Contains(dlq[0].body["exception"].(string), "job data does not match") {
		t.Fatalf("unexpected DLQ: %+v", dlq)
	}
}

func TestPanicIsAFailure(t *testing.T) {
	h := newHarness(t, map[string]Handler{"TestJob": func(context.Context, *Job) (any, error) { panic("kaboom") }}, JobOptions{MaxRetries: NoRetries})
	d, f := newDelivery(nil, nil, false)

	h.proc.handle(d, h.consumer)

	expectSettled(t, f, "ack")
	expectStatuses(t, h, StatusRunning, StatusFailed)

	if !strings.Contains(h.pub.lastStatus()["exception"].(string), "panic: kaboom") {
		t.Fatalf("exception = %v", h.pub.lastStatus()["exception"])
	}
}

func TestTimesOutEvenIfHandlerIgnoresContext(t *testing.T) {
	h := newHarness(t, map[string]Handler{"TestJob": func(context.Context, *Job) (any, error) { select {} }}, JobOptions{})
	d, f := newDelivery(map[string]any{"executionTimeoutSeconds": 0.05}, nil, false)

	h.proc.handle(d, h.consumer)

	expectSettled(t, f, "ack")
	expectStatuses(t, h, StatusRunning, StatusTimedOut)

	if !strings.Contains(h.pub.lastStatus()["exception"].(string), "exceeded maximum timeout of 0.05 seconds") {
		t.Fatalf("exception = %v", h.pub.lastStatus()["exception"])
	}
}

func TestCancelsRunningJob(t *testing.T) {
	started := make(chan context.Context, 1)
	h := newHarness(t, map[string]Handler{"TestJob": func(ctx context.Context, _ *Job) (any, error) {
		started <- ctx
		<-ctx.Done()
		return nil, ctx.Err()
	}}, JobOptions{})
	d, f := newDelivery(nil, nil, false)

	done := make(chan struct{})
	go func() { h.proc.handle(d, h.consumer); close(done) }()

	ctx := <-started

	if !h.cancels.cancel(strings.ToUpper(testOccurrenceID), "Cancelled by user") {
		t.Fatal("cancel did not find the running job")
	}

	<-done

	if !errors.Is(context.Cause(ctx), ErrJobCancelled) {
		t.Fatalf("cause = %v", context.Cause(ctx))
	}

	expectSettled(t, f, "ack")
	expectStatuses(t, h, StatusRunning, StatusCancelled)
}

func TestShutdownReturnsJobToQueue(t *testing.T) {
	started := make(chan struct{})
	h := newHarness(t, map[string]Handler{"TestJob": func(context.Context, *Job) (any, error) { close(started); select {} }}, JobOptions{})
	d, f := newDelivery(nil, nil, false)

	done := make(chan struct{})
	go func() { h.proc.handle(d, h.consumer); close(done) }()

	<-started
	h.cancels.abortAll(ErrWorkerStopping)
	<-done

	expectSettled(t, f, "requeue")
	expectStatuses(t, h, StatusRunning)
}

func TestShutdownDuringRetryDelayRequeues(t *testing.T) {
	h := newHarness(t, map[string]Handler{"TestJob": func(context.Context, *Job) (any, error) { return nil, errors.New("boom") }}, JobOptions{})
	d, f := newDelivery(nil, nil, false)

	h.proc.beginShutdown()
	h.proc.handle(d, h.consumer)

	expectSettled(t, f, "requeue")

	if len(h.pub.to(h.consumer.queue)) != 0 {
		t.Fatal("must not republish while stopping")
	}
}

func TestUnknownJobIsDeadLettered(t *testing.T) {
	h := newHarness(t, map[string]Handler{"TestJob": func(context.Context, *Job) (any, error) { return "ok", nil }}, JobOptions{})
	d, f := newDelivery(map[string]any{"jobNameInWorker": "OtherJob"}, nil, false)

	h.proc.handle(d, h.consumer)

	expectSettled(t, f, "ack")
	expectStatuses(t, h, StatusRunning, StatusFailed)

	if dlq := h.pub.deadLettered(); len(dlq) != 1 || !strings.HasPrefix(dlq[0].body["exception"].(string), "[PERMANENT FAILURE] No IJob implementation registered for type: OtherJob") {
		t.Fatalf("unexpected DLQ: %+v", dlq)
	}
}

func TestJobNamesAreCaseInsensitive(t *testing.T) {
	h := newHarness(t, map[string]Handler{"TestJob": func(context.Context, *Job) (any, error) { return "done", nil }}, JobOptions{})
	d, _ := newDelivery(map[string]any{"jobNameInWorker": "testjob"}, nil, false)

	h.proc.handle(d, h.consumer)

	if h.pub.lastStatus()["result"] != "done" {
		t.Fatalf("result = %v", h.pub.lastStatus()["result"])
	}
}

func TestRejectsBodyThatIsNotAJob(t *testing.T) {
	h := newHarness(t, map[string]Handler{"TestJob": func(context.Context, *Job) (any, error) { return "ok", nil }}, JobOptions{})
	d, f := newDelivery(nil, nil, false)
	d.body = []byte("not json")

	h.proc.handle(d, h.consumer)

	expectSettled(t, f, "nack")

	if len(h.pub.messages) != 0 {
		t.Fatal("nothing should be published for an invalid body")
	}
}

func TestReportsRedelivery(t *testing.T) {
	h := newHarness(t, map[string]Handler{"TestJob": func(context.Context, *Job) (any, error) { return "ok", nil }}, JobOptions{})
	d, _ := newDelivery(nil, nil, true)

	h.proc.handle(d, h.consumer)

	expectStatuses(t, h, StatusRunning, StatusRunning, StatusCompleted)

	for _, l := range h.pub.logs() {
		if l["category"] == "Redelivery" {
			return
		}
	}

	t.Fatal("redelivery log missing")
}

func TestRetriesRefusedFinalStatus(t *testing.T) {
	h := newHarness(t, map[string]Handler{"TestJob": func(context.Context, *Job) (any, error) { return "ok", nil }}, JobOptions{})
	d, _ := newDelivery(nil, nil, false)

	h.pub.failNext = 1
	h.proc.handle(d, h.consumer)

	expectStatuses(t, h, StatusRunning, StatusCompleted)
}

func TestWaitForIdleTracksRunningJobs(t *testing.T) {
	release := make(chan struct{})
	started := make(chan struct{})
	h := newHarness(t, map[string]Handler{"TestJob": func(context.Context, *Job) (any, error) { close(started); <-release; return nil, nil }}, JobOptions{})
	d, _ := newDelivery(nil, nil, false)

	done := make(chan struct{})
	go func() { h.proc.handle(d, h.consumer); close(done) }()

	<-started

	if h.proc.runningJobs() != 1 || h.proc.waitForIdle(10*time.Millisecond) {
		t.Fatal("expected one running job")
	}

	close(release)
	<-done

	if h.proc.runningJobs() != 0 || !h.proc.waitForIdle(10*time.Millisecond) {
		t.Fatal("expected no running jobs")
	}
}
