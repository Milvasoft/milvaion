package milvaion

import (
	"context"
	"encoding/binary"
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"time"
)

type publishOptions struct {
	persistent    bool
	correlationID string
	headers       map[string]any
}

// publisher is the publishing side of the broker, with publisher confirms.
type publisher interface {
	publish(ctx context.Context, exchange, routingKey string, body []byte, o publishOptions) error
}

// delivery is one message taken from a job queue.
type delivery struct {
	body          []byte
	headers       map[string]any
	correlationID string
	redelivered   bool
	ack           func() error
	nack          func(requeue bool) error
}

// cancelRegistry holds the cancel functions of the jobs running on this instance.
type cancelRegistry struct {
	mu      sync.Mutex
	running map[string]context.CancelCauseFunc
}

func newCancelRegistry() *cancelRegistry {
	return &cancelRegistry{running: map[string]context.CancelCauseFunc{}}
}

func (r *cancelRegistry) register(occurrenceID string, cancel context.CancelCauseFunc) {
	r.mu.Lock()
	r.running[strings.ToLower(occurrenceID)] = cancel
	r.mu.Unlock()
}

func (r *cancelRegistry) unregister(occurrenceID string) {
	r.mu.Lock()
	delete(r.running, strings.ToLower(occurrenceID))
	r.mu.Unlock()
}

// cancel returns false when the job is not running on this instance.
func (r *cancelRegistry) cancel(occurrenceID, reason string) bool {
	r.mu.Lock()
	cancel, ok := r.running[strings.ToLower(occurrenceID)]
	r.mu.Unlock()

	if ok {
		if reason != "" {
			cancel(fmt.Errorf("%w: %s", ErrJobCancelled, reason))
		} else {
			cancel(ErrJobCancelled)
		}
	}

	return ok
}

func (r *cancelRegistry) abortAll(cause error) {
	r.mu.Lock()
	defer r.mu.Unlock()

	for _, cancel := range r.running {
		cancel(cause)
	}
}

type processor struct {
	opts           resolvedOptions
	jobs           map[string]*resolvedJob
	pub            publisher
	logs           *logBuffer
	cancels        *cancelRegistry
	onCountChanged func()
	sleep          func(ctx context.Context, d time.Duration) error

	shutdownCtx context.Context
	shutdown    context.CancelFunc

	mu      sync.Mutex
	running int
	waiters []chan struct{}
}

func newProcessor(opts resolvedOptions, jobs map[string]*resolvedJob, pub publisher, logs *logBuffer, cancels *cancelRegistry) *processor {
	ctx, cancel := context.WithCancel(context.Background())

	return &processor{opts: opts, jobs: jobs, pub: pub, logs: logs, cancels: cancels, sleep: sleepContext, shutdownCtx: ctx, shutdown: cancel}
}

func (p *processor) runningJobs() int {
	p.mu.Lock()
	defer p.mu.Unlock()

	return p.running
}

// beginShutdown makes jobs waiting out a retry delay give their message back right away.
func (p *processor) beginShutdown() { p.shutdown() }

// waitForIdle returns true once no job is running, or false after timeout.
func (p *processor) waitForIdle(timeout time.Duration) bool {
	p.mu.Lock()

	if p.running == 0 {
		p.mu.Unlock()
		return true
	}

	done := make(chan struct{})
	p.waiters = append(p.waiters, done)
	p.mu.Unlock()

	timer := time.NewTimer(timeout)
	defer timer.Stop()

	select {
	case <-done:
		return true
	case <-timer.C:
		return false
	}
}

func (p *processor) handle(d delivery, consumer *resolvedJob) {
	p.mu.Lock()
	p.running++
	p.mu.Unlock()
	p.countChanged()

	defer func() {
		if recovered := recover(); recovered != nil {
			p.opts.logger.Error("unexpected panic while processing a job delivery", "panic", recovered)
			p.settle(d.nack(false))
		}

		p.mu.Lock()
		p.running--

		if p.running == 0 {
			for _, w := range p.waiters {
				close(w)
			}

			p.waiters = nil
		}

		p.mu.Unlock()
		p.countChanged()
	}()

	p.process(d, consumer)
}

func (p *processor) countChanged() {
	if p.onCountChanged != nil {
		p.onCountChanged()
	}
}

func (p *processor) process(d delivery, consumer *resolvedJob) {
	var scheduled ScheduledJob

	if err := json.Unmarshal(d.body, &scheduled); err != nil || scheduled.ID == "" || scheduled.JobNameInWorker == "" {
		p.opts.logger.Error("received a message that is not a valid job; sending it to the dead letter queue", "queue", consumer.queue)
		p.settle(d.nack(false))
		return
	}

	occurrenceID := parseOccurrenceID(d.headers, d.correlationID)

	if occurrenceID == "" {
		p.opts.logger.Error("job arrived without an occurrence id; sending it to the dead letter queue", "job", scheduled.ID)
		p.settle(d.nack(false))
		return
	}

	run := &jobRun{p: p, job: scheduled, occurrenceID: occurrenceID, retryCount: parseRetryCount(d.headers)}

	if d.redelivered {
		p.opts.logger.Warn("job was redelivered; the previous attempt may have crashed", "job", scheduled.ID, "occurrence", occurrenceID)
		start := time.Now()
		run.status(StatusRunning, statusFields{start: &start}, true)
		run.log(LevelWarning, "⚠️ Job redelivered - previous execution may have crashed or timed out. Re-executing from the beginning.",
			map[string]any{"Reason": "RabbitMQ redelivery detected", "WorkerId": p.opts.instanceID}, "Redelivery")
	}

	jobCtx, cancel := context.WithCancelCause(context.Background())
	defer cancel(nil)

	p.cancels.register(occurrenceID, cancel)
	defer p.cancels.unregister(occurrenceID)

	p.execute(d, consumer, run, jobCtx, cancel)
}

type outcome struct {
	result string
	err    error
	abort  error // ErrJobCancelled, ErrJobTimedOut or ErrWorkerStopping, possibly wrapped
}

func (p *processor) execute(d delivery, consumer *resolvedJob, run *jobRun, jobCtx context.Context, cancel context.CancelCauseFunc) {
	start := time.Now()

	run.status(StatusRunning, statusFields{start: &start}, true)
	run.flushLogs()

	registered := p.jobs[strings.ToLower(run.job.JobNameInWorker)]

	if registered == nil {
		exception := "No IJob implementation registered for type: " + run.job.JobNameInWorker
		p.opts.logger.Error(exception)

		end := time.Now()
		run.status(StatusFailed, statusFields{end: &end, exception: exception}, true)
		p.deadLetter(d, run, "[PERMANENT FAILURE] "+exception, run.retryCount, 0)

		return
	}

	timeout := registered.executionTimeout

	if s := run.job.ExecutionTimeoutSeconds; s != nil {
		timeout = time.Duration(*s * float64(time.Second))
	}

	result := p.runHandler(registered, run, jobCtx, cancel, timeout)
	end := time.Now()
	timing := statusFields{start: &start, end: &end, duration: end.Sub(start)}

	switch {
	case result.abort != nil:
		p.finishAborted(d, run, result.abort, timing, timeout)
	case result.err != nil:
		p.finishFailed(d, consumer, registered, run, result.err, timing)
	default:
		run.flushLogs()
		timing.result = result.result
		run.status(StatusCompleted, timing, true)
		p.settle(d.ack())
	}
}

func (p *processor) runHandler(registered *resolvedJob, run *jobRun, jobCtx context.Context, cancel context.CancelCauseFunc, timeout time.Duration) outcome {
	if timeout > 0 {
		timer := time.AfterFunc(timeout, func() { cancel(ErrJobTimedOut) })
		defer timer.Stop()
	}

	stopHeartbeat := make(chan struct{})
	defer close(stopHeartbeat)

	if interval := p.opts.jobHeartbeatInterval; interval > 0 {
		go func() {
			ticker := time.NewTicker(interval)
			defer ticker.Stop()

			for {
				select {
				case <-ticker.C:
					run.status(StatusRunning, statusFields{}, false)
				case <-stopHeartbeat:
					return
				}
			}
		}()
	}

	job := &Job{
		OccurrenceID: run.occurrenceID,
		RetryCount:   run.retryCount,
		InstanceID:   p.opts.instanceID,
		Scheduled:    run.job,
		sink:         func(l occurrenceLog) { p.logs.add(run.occurrenceID, p.opts.instanceID, l) },
	}

	done := make(chan outcome, 1)

	// A handler that ignores its context keeps running in the background, but the job is settled as
	// soon as it is aborted so its slot and message are released.
	go func() {
		defer func() {
			if recovered := recover(); recovered != nil {
				done <- outcome{err: recoverToError(recovered)}
			}
		}()

		value, err := registered.handler(jobCtx, job)

		if err != nil {
			done <- outcome{err: err}
			return
		}

		done <- outcome{result: serializeResult(value, run.job.JobNameInWorker)}
	}()

	select {
	case result := <-done:
		if cause := context.Cause(jobCtx); result.err != nil && isAbort(cause) {
			return outcome{abort: cause}
		}

		return result
	case <-jobCtx.Done():
		return outcome{abort: context.Cause(jobCtx)}
	}
}

func isAbort(cause error) bool {
	return errors.Is(cause, ErrJobCancelled) || errors.Is(cause, ErrJobTimedOut) || errors.Is(cause, ErrWorkerStopping)
}

func (p *processor) finishAborted(d delivery, run *jobRun, cause error, timing statusFields, timeout time.Duration) {
	switch {
	case errors.Is(cause, ErrWorkerStopping):
		// Nobody cancelled this job; it goes back to the queue and runs again.
		p.opts.logger.Warn("job was interrupted by worker shutdown; returning it to the queue", "job", run.job.ID, "occurrence", run.occurrenceID)
		run.flushLogs()
		p.settle(d.nack(true))

		return

	case errors.Is(cause, ErrJobTimedOut):
		seconds := formatSeconds(timeout)
		run.log(LevelError, fmt.Sprintf("Job execution exceeded timeout limit of %s seconds", seconds), nil, "UserDefined")
		run.flushLogs()
		timing.exception = fmt.Sprintf("Job execution exceeded maximum timeout of %s seconds (%dms elapsed)", seconds, timing.duration.Milliseconds())
		run.status(StatusTimedOut, timing, true)

	default:
		run.log(LevelWarning, "Job execution was cancelled", map[string]any{"Reason": cause.Error()}, "UserCode")
		run.flushLogs()
		timing.result = "Job cancelled by user or system"
		run.status(StatusCancelled, timing, true)
	}

	p.settle(d.ack())
}

func (p *processor) finishFailed(d delivery, consumer, registered *resolvedJob, run *jobRun, err error, timing statusFields) {
	exception := formatError(err)

	if IsPermanent(err) {
		run.logError("Permanent job failure (will not retry): "+err.Error(), err)
		run.log(LevelError, "❌ Permanent failure - job will not be retried. Moving to Dead Letter Queue (DLQ).",
			map[string]any{"FailureType": "Permanent", "FinalStatus": "MovedToDLQ"}, "Retry")
		run.flushLogs()
		timing.exception = exception
		run.status(StatusFailed, timing, true)
		p.deadLetter(d, run, "[PERMANENT FAILURE] "+exception, run.retryCount, 0)

		return
	}

	run.logError("Job execution failed: "+err.Error(), err)

	if run.retryCount < registered.maxRetries {
		delay := registered.baseRetryDelay * time.Duration(math.Pow(2, float64(run.retryCount)))

		run.log(LevelWarning, fmt.Sprintf("⚠️ Job failed (attempt %d/%d). Retrying in %ss...", run.retryCount+1, registered.maxRetries, formatSeconds(delay)), map[string]any{
			"RetryAttempt":  run.retryCount + 1,
			"MaxRetries":    registered.maxRetries,
			"DelaySeconds":  delay.Seconds(),
			"NextRetryTime": time.Now().Add(delay).UTC().Format(time.RFC3339Nano),
		}, "Retry")
		run.flushLogs()

		p.retry(d, consumer, run, delay, registered.maxRetries)

		return
	}

	final := fmt.Sprintf("Max retries (%d) exceeded. Original exception: %s", registered.maxRetries, exception)

	run.log(LevelError, fmt.Sprintf("❌ Job failed after %d retries. Moving to Dead Letter Queue (DLQ).", registered.maxRetries),
		map[string]any{"TotalRetries": registered.maxRetries, "FinalStatus": "MovedToDLQ"}, "Retry")
	run.flushLogs()

	end := time.Now()
	timing.end = &end
	timing.exception = final
	run.status(StatusFailed, timing, true)
	p.deadLetter(d, run, final, registered.maxRetries, registered.maxRetries)
}

// retry waits out the backoff, republishes the job with a higher retry count and acks the original.
// The original stays unacked meanwhile, so a crash during the wait gets it redelivered.
func (p *processor) retry(d delivery, consumer *resolvedJob, run *jobRun, delay time.Duration, maxRetries int) {
	interval := p.opts.jobHeartbeatInterval
	remaining := delay

	for remaining > 0 {
		step := remaining

		if interval > 0 && interval < step {
			step = interval
		}

		if err := p.sleep(p.shutdownCtx, step); err != nil {
			p.opts.logger.Warn("worker is stopping during a retry delay; returning the job to the queue", "job", run.job.ID)
			p.settle(d.nack(true))

			return
		}

		remaining -= step

		if remaining > 0 && interval > 0 {
			run.status(StatusRunning, statusFields{}, false)
		}
	}

	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()

	err := p.pub.publish(ctx, "", consumer.queue, d.body, publishOptions{
		persistent:    true,
		correlationID: run.occurrenceID,
		headers:       map[string]any{"CorrelationId": run.occurrenceID, "x-retry-count": int32(run.retryCount + 1), "MaxRetries": int32(maxRetries)},
	})

	if err != nil {
		p.opts.logger.Error("failed to republish a job for retry; dead-lettering the message", "job", run.job.ID, "error", err)
		p.settle(d.nack(false))

		return
	}

	p.settle(d.ack())
}

func (p *processor) deadLetter(d delivery, run *jobRun, exception string, retryCount, maxRetries int) {
	body, _ := json.Marshal(dlqMessage{
		ID:              run.job.ID,
		DisplayName:     run.job.DisplayName,
		JobNameInWorker: run.job.JobNameInWorker,
		JobData:         run.job.JobData,
		ExecuteAt:       run.job.ExecuteAt,
		Status:          StatusFailed,
		Exception:       exception,
	})

	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()

	err := p.pub.publish(ctx, exchangeDeadLetter, deadLetterRoutingKey, body, publishOptions{
		persistent:    true,
		correlationID: run.occurrenceID,
		headers:       map[string]any{"CorrelationId": run.occurrenceID, "x-retry-count": int32(retryCount), "MaxRetries": int32(maxRetries)},
	})

	if err != nil {
		// The queue dead-letters a rejected message itself, just without the exception details.
		p.opts.logger.Error("failed to publish to the dead letter queue; rejecting the message instead", "job", run.job.ID, "error", err)
		p.settle(d.nack(false))

		return
	}

	p.settle(d.ack())
}

func (p *processor) settle(err error) {
	if err != nil {
		// An ack or nack on a channel that has closed: the broker redelivers the message.
		p.opts.logger.Warn("could not settle a message; the broker will redeliver it", "error", err)
	}
}

type statusFields struct {
	start, end *time.Time
	duration   time.Duration
	result     string
	exception  string
}

// jobRun holds the state and reporting helpers of one execution.
type jobRun struct {
	p            *processor
	job          ScheduledJob
	occurrenceID string
	retryCount   int
}

func (r *jobRun) log(level LogLevel, message string, data map[string]any, category string) {
	r.p.logs.add(r.occurrenceID, r.p.opts.instanceID, occurrenceLog{Timestamp: now(), Level: level, Message: message, Data: data, Category: category})
}

func (r *jobRun) logError(message string, err error) {
	t := errorType(err)
	data := map[string]any{"ExceptionType": t, "StackTrace": formatError(err)}

	if inner := errors.Unwrap(err); inner != nil {
		data["InnerException"] = inner.Error()
	}

	r.p.logs.add(r.occurrenceID, r.p.opts.instanceID, occurrenceLog{Timestamp: now(), Level: LevelError, Message: message, Data: data, Category: "UserDefined", ExceptionType: &t})
}

func (r *jobRun) flushLogs() {
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()

	if err := r.p.logs.flush(ctx); err != nil {
		r.p.opts.logger.Warn("failed to flush job logs; they stay buffered and will be retried", "error", err)
	}
}

// status publishes a status update. Final statuses are retried, because losing one leaves the
// occurrence running until the zombie detector gives up on it.
func (r *jobRun) status(status Status, f statusFields, retry bool) {
	message := statusUpdateMessage{
		OccurrenceID:     r.occurrenceID,
		JobID:            r.job.ID,
		WorkerID:         r.p.opts.workerID,
		InstanceID:       r.p.opts.instanceID,
		Status:           status,
		MessageTimestamp: now(),
	}

	if f.start != nil {
		message.StartTime = timeString(*f.start)
	}

	if f.end != nil {
		message.EndTime = timeString(*f.end)
		ms := f.duration.Milliseconds()
		message.DurationMs = &ms
	}

	if f.result != "" {
		message.Result = &f.result
	}

	if f.exception != "" {
		message.Exception = &f.exception
	}

	body, _ := json.Marshal(message)
	delays := []time.Duration{500 * time.Millisecond, 2 * time.Second, 5 * time.Second}

	if !retry {
		delays = nil
	}

	for attempt := 0; ; attempt++ {
		ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
		err := r.p.pub.publish(ctx, "", queueStatusUpdates, body, publishOptions{persistent: true})
		cancel()

		if err == nil {
			return
		}

		if attempt >= len(delays) {
			r.p.opts.logger.Warn("failed to publish a status update", "status", int(status), "occurrence", r.occurrenceID, "error", err)
			return
		}

		time.Sleep(delays[attempt])
	}
}

var guidPattern = regexp.MustCompile(`^(?i)[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$`)

// parseOccurrenceID reads the OccurrenceId header, the CorrelationId header or the correlation id property.
func parseOccurrenceID(headers map[string]any, correlationID string) string {
	for _, candidate := range []string{headerString(headers["OccurrenceId"]), headerString(headers["CorrelationId"]), correlationID} {
		if candidate = strings.TrimSpace(candidate); guidPattern.MatchString(candidate) {
			return candidate
		}
	}

	return ""
}

func headerString(value any) string {
	switch v := value.(type) {
	case string:
		return v
	case []byte:
		return string(v)
	default:
		return ""
	}
}

// parseRetryCount reads x-retry-count in the encodings brokers and clients use.
func parseRetryCount(headers map[string]any) int {
	switch v := headers["x-retry-count"].(type) {
	case int8:
		return max(0, int(v))
	case int16:
		return max(0, int(v))
	case int32:
		return max(0, int(v))
	case int64:
		return max(0, int(v))
	case int:
		return max(0, v)
	case uint8:
		return int(v)
	case uint16:
		return int(v)
	case uint32:
		return int(v)
	case []byte:
		// The .NET SDK reads a 4-byte value as a little-endian int.
		if len(v) >= 4 {
			return max(0, int(int32(binary.LittleEndian.Uint32(v))))
		}
	case string:
		if n, err := strconv.Atoi(v); err == nil {
			return max(0, n)
		}
	}

	return 0
}

func serializeResult(value any, jobName string) string {
	switch v := value.(type) {
	case nil:
		return "Job " + jobName + " completed successfully"
	case string:
		return v
	case []byte:
		return string(v)
	case json.RawMessage:
		return string(v)
	}

	b, err := json.Marshal(value)

	if err != nil {
		return fmt.Sprint(value)
	}

	return string(b)
}

func formatSeconds(d time.Duration) string {
	return strconv.FormatFloat(d.Seconds(), 'f', -1, 64)
}

func sleepContext(ctx context.Context, d time.Duration) error {
	timer := time.NewTimer(d)
	defer timer.Stop()

	select {
	case <-timer.C:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}
