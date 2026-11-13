package milvaion

import (
	"errors"
	"fmt"
	"reflect"
	"runtime/debug"
)

// PermanentError marks a failure that retrying cannot fix, such as invalid job data.
// A job that returns one is not retried and goes straight to the dead letter queue.
type PermanentError struct {
	Err error
}

func (e *PermanentError) Error() string { return e.Err.Error() }
func (e *PermanentError) Unwrap() error { return e.Err }

// Permanent wraps err so the job is not retried. Returns nil for a nil err.
func Permanent(err error) error {
	if err == nil {
		return nil
	}

	return &PermanentError{Err: err}
}

// Permanentf is Permanent(fmt.Errorf(format, args...)).
func Permanentf(format string, args ...any) error {
	return Permanent(fmt.Errorf(format, args...))
}

// IsPermanent reports whether err or any error it wraps is a PermanentError.
func IsPermanent(err error) bool {
	var permanent *PermanentError

	return errors.As(err, &permanent)
}

// Causes of a job's context being cancelled. Read them with context.Cause(ctx).
var (
	// ErrJobCancelled: the job was cancelled from the dashboard.
	ErrJobCancelled = errors.New("milvaion: job was cancelled")
	// ErrJobTimedOut: the job ran past its execution timeout.
	ErrJobTimedOut = errors.New("milvaion: job exceeded its execution timeout")
	// ErrWorkerStopping: the worker is shutting down; the job goes back to the queue.
	ErrWorkerStopping = errors.New("milvaion: worker is shutting down")
)

// panicError is a recovered panic from a job handler.
type panicError struct {
	value any
	stack []byte
}

func (e *panicError) Error() string { return fmt.Sprintf("panic: %v", e.value) }

// formatError renders an error the way the .NET SDK formats exceptions, so the dashboard shows both alike.
func formatError(err error) string {
	if err == nil {
		return ""
	}

	var stack string

	if p, ok := err.(*panicError); ok {
		stack = string(p.stack)
	}

	formatted := fmt.Sprintf("Type: %s\nMessage: %s\nStackTrace: %s\n", errorType(err), err.Error(), stack)

	if inner := errors.Unwrap(err); inner != nil {
		formatted += "\nInner Exception:\n" + formatError(inner)
	}

	return formatted
}

func errorType(err error) string {
	if _, ok := err.(*panicError); ok {
		return "panic"
	}

	t := reflect.TypeOf(err)

	for t.Kind() == reflect.Pointer {
		t = t.Elem()
	}

	if t.PkgPath() == "" {
		return t.String()
	}

	return t.PkgPath() + "." + t.Name()
}

func recoverToError(recovered any) error {
	if err, ok := recovered.(error); ok {
		return &panicError{value: err, stack: debug.Stack()}
	}

	return &panicError{value: recovered, stack: debug.Stack()}
}
