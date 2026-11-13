# Milvaion Go Worker SDK

Run [Milvaion](https://github.com/Milvasoft/milvaion) jobs in Go. A Go worker shows up in the dashboard like a .NET worker. The scheduler dispatches jobs to it, and the dashboard shows its live logs, status, retries, cancellation and timeouts.

```bash
go get github.com/Milvasoft/milvaion/sdks/go/milvaion
```

It requires Go 1.24 or later, and connects to the same RabbitMQ and Redis instances as your Milvaion API.

## Quick start

```go
package main

import (
	"context"
	"log"
	"os"
	"os/signal"
	"syscall"

	"github.com/Milvasoft/milvaion/sdks/go/milvaion"
)

type emailData struct {
	To      string `json:"to"`
	Subject string `json:"subject"`
}

func main() {
	worker, err := milvaion.NewWorker(milvaion.Options{
		WorkerID:    "mail-worker",
		RabbitMQURL: "amqp://guest:guest@localhost:5672/",
		RedisURL:    "redis://localhost:6379",
	})
	if err != nil {
		log.Fatal(err)
	}

	err = worker.Handle("SendEmailJob", func(ctx context.Context, job *milvaion.Job) (any, error) {
		var data emailData
		if err := job.Bind(&data); err != nil {
			return nil, err // invalid JSON is already a permanent error
		}

		if data.To == "" {
			return nil, milvaion.Permanentf(`job data needs "to"`) // no retries, straight to the DLQ
		}

		job.Logf("Sending %q to %s", data.Subject, data.To)

		if err := send(ctx, data); err != nil { // pass ctx on: it is cancelled on cancel, timeout or shutdown
			return nil, err // retried with exponential backoff
		}

		return map[string]any{"sent": true}, nil // shown as the occurrence result
	})
	if err != nil {
		log.Fatal(err)
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	// Blocks until ctx is cancelled, then shuts down gracefully.
	if err := worker.Run(ctx); err != nil {
		log.Fatal(err)
	}
}
```

Then create a job in the dashboard: pick the worker `mail-worker` and the job `SendEmailJob`, and set a schedule.

## Jobs

`Handle(name, handler, options...)` registers a handler under the name the dashboard dispatches it by. Names are unique within a worker and matched case-insensitively.

The handler receives a context and a `*Job`:

| Member | Description |
|---|---|
| `job.Bind(&v)` | Decodes the job data. Data that does not match returns a permanent error. |
| `job.RawData()` | Job data as the raw JSON string. |
| `job.OccurrenceID` | Id of this execution. It stays the same across retries and redeliveries, so you can use it as an idempotency key. |
| `job.RetryCount` | `0` on the first attempt, then `1`, `2` and so on. |
| `job.Log` / `Logf` / `Debug` / `Warn` / `Error(msg, err, data)` | Write to the occurrence log in the dashboard. |
| `job.Scheduled` | The scheduled job as the API sent it. |

The context is cancelled when the job is cancelled from the dashboard, times out or the worker shuts down. `context.Cause(ctx)` is then `milvaion.ErrJobCancelled`, `ErrJobTimedOut` or `ErrWorkerStopping`.

How the handler ends decides the outcome:

| The handler... | Outcome |
|---|---|
| returns a value | `Completed`. A string is stored as-is; anything else is stored as JSON. |
| returns an error | Retried with exponential backoff. After `MaxRetries`, the occurrence is `Failed` and the job goes to the dead letter queue. |
| returns `milvaion.Permanent(err)` | `Failed` without retrying, and the job goes to the dead letter queue. |
| panics | Treated as a returned error. |
| runs past its timeout | `TimedOut` |
| is cancelled from the dashboard | `Cancelled` |
| is still running when the worker stops | The job goes back to the queue and runs again. |

When a job is aborted, the occurrence settles right away, even if the handler ignores its context. A handler that ignores it keeps its goroutine running, though. Pass `ctx` on so the work actually stops.

### Job options

| Field | Default | Description |
|---|---|---|
| `MaxParallelJobs` | min(10, worker `MaxParallelJobs`) | Jobs of this type that may run at once on this instance |
| `ExecutionTimeout` | worker default (1h) | Negative disables the timeout. A timeout set on the job in the dashboard takes precedence. |
| `MaxRetries` | 3 | Retries after the first failure. `milvaion.NoRetries` disables them. |
| `BaseRetryDelay` | 5s | Retry *n* waits `base × 2^(n-1)` |
| `DataSchema` | none | JSON Schema of the job data, as a map, struct, string or `[]byte`. The dashboard builds the job data form from it. |
| `ResultSchema` | none | JSON Schema of the result |
| `RoutingPattern` | `{workerId}.{name without "Job"}.*` | RabbitMQ routing pattern |
| `ConsumerID` | `{name without "Job"}-consumer` | Shown in the dashboard |

## Worker options

| Field | Default | Description |
|---|---|---|
| `WorkerID` | required | Identifies the worker application. Use the same value on every replica. |
| `RabbitMQURL` | `amqp://guest:guest@localhost:5672/` | |
| `QueueType` | `classic` | Must match the API's `MilvaionConfig:RabbitMQ:QueueType` |
| `RedisURL`, `DisableRedis` | `redis://localhost:6379` | Redis only carries cancellation requests |
| `RedisKeyPrefix` | `Milvaion:JobScheduler:` | Must match the API's `MilvaionConfig:Redis:KeyPrefix` |
| `MaxParallelJobs` | CPU count × 2 | Capacity reported for this instance |
| `ExecutionTimeout` | 1h | Default job timeout |
| `HeartbeatInterval` | 30s | |
| `JobHeartbeatInterval` | 60s | Keeps long jobs from being flagged as zombies |
| `ShutdownTimeout` | 30s | How long `Run` lets running jobs finish after its context is cancelled |
| `StartupTimeout` | 30s | How long `Run` waits for RabbitMQ and Redis |
| `Version` | `1.0.0` | Shown in the dashboard |
| `Logger` | `slog.Default()` | |

For containers, `milvaion.OptionsFromEnv(base)` reads the following variables:

- `MILVAION_WORKER_ID`
- `MILVAION_RABBITMQ_URL`
- `MILVAION_RABBITMQ_QUEUE_TYPE`
- `MILVAION_REDIS_URL` (`false` disables Redis)
- `MILVAION_REDIS_KEY_PREFIX`
- `MILVAION_MAX_PARALLEL_JOBS`
- `MILVAION_EXECUTION_TIMEOUT_SECONDS`
- `MILVAION_HEARTBEAT_INTERVAL_SECONDS`

## Reliability

- **Reconnects.** If RabbitMQ goes away, the worker reconnects with backoff, then redeclares its queues and registers again. Redis reconnects and resubscribes on its own.
- **At-least-once delivery.** A message is acknowledged only after its outcome has been reported, so a crash makes the job run again. Make handlers idempotent; `job.OccurrenceID` helps with that.
- **Graceful shutdown.** When `Run`'s context is cancelled, the worker does the following in order:
  1. Stops taking new jobs.
  2. Waits up to `ShutdownTimeout` for running jobs to finish.
  3. Returns the jobs that are still running to the queue.
  4. Flushes logs and tells the API the instance has left.

## How it works

The SDK implements the language-neutral [worker protocol](../../../docs/githubdocs/WORKER-PROTOCOL.md): the RabbitMQ queues and message formats the .NET SDK uses.

## Development

```bash
go test ./...
go run ./examples/basic   # configure with MILVAION_* variables
```
