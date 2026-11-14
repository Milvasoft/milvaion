# Milvaion Go Worker SDK

Run [Milvaion](https://github.com/Milvasoft/milvaion) jobs in Go. A Go worker shows up in the dashboard like a .NET worker. The scheduler dispatches jobs to it, and the dashboard shows its live logs, status, retries, cancellation and timeouts.

```bash
go get github.com/Milvasoft/milvaion/sdks/go/milvaion
```

It requires Go 1.24 or later, and connects to the same broker (RabbitMQ, or [Kafka](#kafka)) and Redis instances as your Milvaion API.

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
| returns `milvaion.RetryAfter(at, msg)` | `Failed` with the requested time, no local retry and no dead letter queue. See [Retry at a specific time](#retry-at-a-specific-time). |
| panics | Treated as a returned error. |
| runs past its timeout | `TimedOut` |
| is cancelled from the dashboard | `Cancelled` |
| is still running when the worker stops | The job goes back to the queue and runs again. |

When a job is aborted, the occurrence settles right away, even if the handler ignores its context. A handler that ignores it keeps its goroutine running, though. Pass `ctx` on so the work actually stops.

### Retry at a specific time

When retrying soon cannot help but retrying later can, return `milvaion.RetryAfter` with the time the job should run again, for example when an API quota resets:

```go
if errors.Is(err, errQuotaExceeded) {
	// The YouTube quota resets at 11:00 UTC.
	now := time.Now().UTC()
	resetAt := time.Date(now.Year(), now.Month(), now.Day(), 11, 0, 0, 0, time.UTC)

	if !resetAt.After(now) {
		resetAt = resetAt.AddDate(0, 0, 1)
	}

	return nil, milvaion.RetryAfter(resetAt, "YouTube quota exhausted")
}
```

The worker does not retry the job and does not send it to the dead letter queue. It reports the occurrence as `Failed` with the requested time (`retryAfter`, UTC) and logs a warning. Detection uses `errors.As`, so a wrapped `*milvaion.RetryAfterError` counts too.

- In a workflow step, Milvaion runs the step again at that time. The workflow's `MaxRetryAfter` setting caps how many times (default 10), and these reruns do not count against `MaxStepRetries`.
- In a standalone job, the occurrence ends as `Failed` and is not retried.

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
| `Transport` | `rabbitmq` | `rabbitmq` or `kafka`. Must match the API. |
| `Kafka` | see [Kafka](#kafka) | Kafka settings, used when `Transport` is `kafka` |
| `RabbitMQURL` | `amqp://guest:guest@localhost:5672/` | |
| `QueueType` | `classic` | Must match the API's `MilvaionConfig:RabbitMQ:QueueType` |
| `RedisURL`, `DisableRedis` | `redis://localhost:6379` | Redis only carries cancellation requests |
| `RedisKeyPrefix` | `Milvaion:JobScheduler:` | Must match the API's `MilvaionConfig:Redis:KeyPrefix` |
| `MaxParallelJobs` | CPU count × 2 | Capacity reported for this instance |
| `ExecutionTimeout` | 1h | Default job timeout |
| `HeartbeatInterval` | 30s | |
| `JobHeartbeatInterval` | 60s | Keeps long jobs from being flagged as zombies |
| `ShutdownTimeout` | 30s | How long `Run` lets running jobs finish after its context is cancelled |
| `StartupTimeout` | 30s | How long `Run` waits for the broker and Redis |
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
- `MILVAION_TRANSPORT` and the Kafka variables listed under [Kafka](#kafka)

## Kafka

RabbitMQ is the default broker. A deployment can use Apache Kafka instead. The API and every worker of a deployment must use the same broker, so switch the API to Kafka too (`MilvaionConfig:Transport:Type`). Redis still carries cancellation requests, as on RabbitMQ.

```go
worker, err := milvaion.NewWorker(milvaion.Options{
	WorkerID:  "mail-worker",
	Transport: "kafka",
	Kafka: milvaion.KafkaOptions{
		Brokers:     []string{"kafka-1:9092", "kafka-2:9092"},
		TopicPrefix: "prod.", // must match the API's
	},
	RedisURL: "redis://localhost:6379",
})
```

Handlers do not change. Jobs, outcomes, retries, logs and cancellation behave as on RabbitMQ.

| `KafkaOptions` field | Default | Description |
|---|---|---|
| `Brokers` | `localhost:9092` | Bootstrap servers. An entry may be a comma separated list. |
| `TLS` | off | Turns TLS on. Setting any of the three files below turns it on too. |
| `TLSCAFile` | system roots | PEM bundle used to verify the brokers |
| `TLSCertFile`, `TLSKeyFile` | none | PEM client certificate and key for mutual TLS |
| `SASLMechanism` | none | `plain`, `scram-sha-256` or `scram-sha-512`, with `SASLUsername` and `SASLPassword` |
| `TopicPrefix` | empty | Put in front of every topic and consumer group name. Only `a-z A-Z 0-9 . _ -`. Must match the API's. |
| `DisableAutoCreateTopics` | `false` | By default the worker creates its topics at startup |
| `NumPartitions` | 12 | Partitions of the topics the worker creates |
| `ReplicationFactor` | -1 | Replication of the topics the worker creates. -1 uses the broker's default. |
| `MaxInFlightPerPartition` | worker `MaxParallelJobs` | Jobs of one partition that may run at the same time |

`OptionsFromEnv` reads them from `MILVAION_TRANSPORT` (`kafka`), `MILVAION_KAFKA_BOOTSTRAP_SERVERS` (or `KAFKA_BOOTSTRAP_SERVERS`), `MILVAION_KAFKA_TLS` (`true`), `MILVAION_KAFKA_TLS_CA_FILE`, `MILVAION_KAFKA_TLS_CERT_FILE`, `MILVAION_KAFKA_TLS_KEY_FILE`, `MILVAION_KAFKA_SASL_MECHANISM`, `MILVAION_KAFKA_SASL_USERNAME`, `MILVAION_KAFKA_SASL_PASSWORD`, `MILVAION_KAFKA_TOPIC_PREFIX`, `MILVAION_KAFKA_AUTO_CREATE_TOPICS` (`false` disables it), `MILVAION_KAFKA_NUM_PARTITIONS` (or `MILVAION_KAFKA_PARTITIONS`), `MILVAION_KAFKA_REPLICATION_FACTOR` and `MILVAION_KAFKA_MAX_IN_FLIGHT_PER_PARTITION`.

How it works on Kafka:

- **Topics.** The worker reads `milvaion.jobs.{worker}` and `milvaion.jobs.{worker}.retry`, where `{worker}` is the worker id in lower case with every character outside `a-z 0-9 . _ -` replaced by `_`. It writes to `milvaion.status`, `milvaion.logs`, `milvaion.heartbeats`, `milvaion.registrations` and `milvaion.dlq`. All names get the topic prefix.
- **Instances.** Every instance of a worker joins the consumer group `milvaion.worker.{worker}`. Partitions are shared between the instances, so a topic with 12 partitions serves at most 12 instances. Raise `NumPartitions` before you scale out further.
- **Parallelism.** An instance runs up to `MaxParallelJobs` jobs at once, also several from one partition, up to `MaxInFlightPerPartition`. The per job `MaxParallelJobs` of `JobOptions` applies to RabbitMQ only. When the instance is full, it pauses the partitions and keeps polling, so it stays in the group.
- **Commits.** A job record is committed only after its outcome has been reported. Kafka commits a position, not a single record, so a partition is committed up to its oldest unfinished job. If an instance crashes, every record after that one runs again, including jobs that had already finished. Make handlers idempotent.
- **Retries.** A failed attempt with retries left is written to the retry topic with a time before which it must not run (`base × 2^n`). The instance slot is free during the wait. The instance that reads the retry holds it until it is due, and the occurrence keeps sending job heartbeats meanwhile.
- **Dead letters.** A job that fails for good goes to `milvaion.dlq` as on RabbitMQ. A record that is not a valid job is forwarded there unchanged, with the headers `x-original-topic` and `x-dlq-reason`.
- **Shutdown.** The worker stops taking records, waits up to `ShutdownTimeout`, writes the jobs that are still running back to their topic, commits and leaves the group.
- **Priority** is best effort: records of a partition are read in the order the API wrote them.

The full wire contract is in [KAFKA-TRANSPORT.md](https://github.com/Milvasoft/milvaion/blob/master/docs/githubdocs/KAFKA-TRANSPORT.md).

## Reliability

- **Reconnects.** If RabbitMQ goes away, the worker reconnects with backoff, then redeclares its queues and registers again. Redis reconnects and resubscribes on its own.
- **At-least-once delivery.** A message is acknowledged only after its outcome has been reported, so a crash makes the job run again. Make handlers idempotent; `job.OccurrenceID` helps with that.
- **Graceful shutdown.** When `Run`'s context is cancelled, the worker does the following in order:
  1. Stops taking new jobs.
  2. Waits up to `ShutdownTimeout` for running jobs to finish.
  3. Returns the jobs that are still running to the queue.
  4. Flushes logs and tells the API the instance has left.

## How it works

The SDK implements the language-neutral [worker protocol](https://github.com/Milvasoft/milvaion/blob/master/docs/githubdocs/WORKER-PROTOCOL.md): the RabbitMQ queues and message formats the .NET SDK uses.

## Development

```bash
go test ./...
go run ./examples/basic   # configure with MILVAION_* variables
```

The Kafka integration tests run against a real broker and are skipped unless `MILVAION_KAFKA_BROKERS` is set. `kafka_integration_test.go` shows a `docker run` line for a single-node broker.

```bash
MILVAION_KAFKA_BROKERS=localhost:29092 go test -run Kafka ./...
```
