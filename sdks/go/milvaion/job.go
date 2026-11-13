package milvaion

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"strings"
	"time"
)

// Handler runs one job execution. Its return value becomes the occurrence result: a string is
// stored as-is and anything else as JSON. Return an error to fail the attempt and have it retried,
// or wrap it with Permanent to skip retries.
//
// ctx is cancelled when the job is cancelled from the dashboard, times out or the worker shuts
// down; context.Cause(ctx) is ErrJobCancelled, ErrJobTimedOut or ErrWorkerStopping. The occurrence
// is settled as soon as that happens, so pass ctx on to everything that can block.
type Handler func(ctx context.Context, job *Job) (any, error)

// Job is what a handler receives.
type Job struct {
	// OccurrenceID identifies this execution. It stays the same across retries and redeliveries,
	// so it works as an idempotency key.
	OccurrenceID string
	// RetryCount is 0 on the first attempt, then 1, 2, ...
	RetryCount int
	// InstanceID of this worker process.
	InstanceID string
	// Scheduled is the job as the API sent it.
	Scheduled ScheduledJob

	sink func(occurrenceLog)
}

// ID of the scheduled job.
func (j *Job) ID() string { return j.Scheduled.ID }

// Name the job is registered under (JobNameInWorker).
func (j *Job) Name() string { return j.Scheduled.JobNameInWorker }

// RawData is the job data as a JSON string, or "" when the job has none.
func (j *Job) RawData() string {
	if j.Scheduled.JobData == nil {
		return ""
	}

	return *j.Scheduled.JobData
}

// Bind decodes the job data into v. Data that is not valid JSON returns a PermanentError, so
// returning the error from the handler fails the job without retrying it.
func (j *Job) Bind(v any) error {
	raw := strings.TrimSpace(j.RawData())

	if raw == "" {
		return nil
	}

	if err := json.Unmarshal([]byte(raw), v); err != nil {
		return Permanent(fmt.Errorf("job data does not match %T: %w", v, err))
	}

	return nil
}

// Log writes an Information entry to the occurrence log. data may be nil.
func (j *Job) Log(message string, data map[string]any) {
	j.write(LevelInformation, message, data, "UserCode", nil)
}

// Debug writes a Debug entry to the occurrence log.
func (j *Job) Debug(message string, data map[string]any) {
	j.write(LevelDebug, message, data, "UserCode", nil)
}

// Warn writes a Warning entry to the occurrence log.
func (j *Job) Warn(message string, data map[string]any) {
	j.write(LevelWarning, message, data, "UserCode", nil)
}

// Error writes an Error entry, with the details of err when it is not nil.
func (j *Job) Error(message string, err error, data map[string]any) {
	details := map[string]any{}

	for k, v := range data {
		details[k] = v
	}

	var exceptionType *string

	if err != nil {
		t := errorType(err)
		exceptionType = &t
		details["ExceptionType"] = t
		details["StackTrace"] = formatError(err)

		if inner := errors.Unwrap(err); inner != nil {
			details["InnerException"] = inner.Error()
		}
	}

	j.write(LevelError, message, details, "UserDefined", exceptionType)
}

// Logf is Log(fmt.Sprintf(format, args...), nil).
func (j *Job) Logf(format string, args ...any) { j.Log(fmt.Sprintf(format, args...), nil) }

func (j *Job) write(level LogLevel, message string, data map[string]any, category string, exceptionType *string) {
	if j.sink == nil {
		return
	}

	j.sink(occurrenceLog{
		Timestamp:     now(),
		Level:         level,
		Message:       message,
		Data:          data,
		Category:      category,
		ExceptionType: exceptionType,
	})
}

func now() string { return time.Now().UTC().Format(time.RFC3339Nano) }

func timeString(t time.Time) *string {
	s := t.UTC().Format(time.RFC3339Nano)

	return &s
}

// schemaString turns a JSON Schema given as a string, bytes or any JSON-marshalable value into a string.
func schemaString(schema any) (string, error) {
	switch s := schema.(type) {
	case nil:
		return "", nil
	case string:
		return s, nil
	case []byte:
		return string(s), nil
	case json.RawMessage:
		return string(s), nil
	}

	b, err := json.Marshal(schema)

	if err != nil {
		return "", err
	}

	return string(b), nil
}
