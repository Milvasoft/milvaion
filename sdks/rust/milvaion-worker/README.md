# Milvaion Rust Worker SDK

Run [Milvaion](https://github.com/Milvasoft/milvaion) jobs in Rust. A Rust worker shows up in the dashboard like a .NET worker. The scheduler dispatches jobs to it, and the dashboard shows its live logs, status, retries, cancellation and timeouts.

```bash
cargo add milvaion-worker
cargo add tokio --features macros,rt-multi-thread
cargo add serde --features derive
```

It requires Rust 1.89 or later and the [Tokio](https://tokio.rs) runtime, and connects to the same RabbitMQ and Redis instances as your Milvaion API.

## Quick start

```rust
use milvaion_worker::{JobContext, JobError, Worker, WorkerOptions};
use serde::Deserialize;
use serde_json::json;

#[derive(Deserialize)]
struct Email {
    to: Option<String>,
    subject: String,
}

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    let mut options = WorkerOptions::new("mail-worker");
    options.rabbitmq_url = "amqp://guest:guest@localhost:5672/".into();
    options.redis_url = Some("redis://localhost:6379".into());

    let mut worker = Worker::new(options)?;

    worker.handle("SendEmailJob", |ctx: JobContext| async move {
        let email: Email = ctx.data()?; // data that does not match is a permanent error

        let to = email.to.ok_or_else(|| JobError::permanent("job data needs \"to\""))?; // no retries, straight to the DLQ

        ctx.info(format!("Sending {:?} to {to}", email.subject));

        tokio::select! {
            sent = send(&to, &email.subject) => sent?, // any error converts with ?; it is retried with backoff
            _ = ctx.cancelled() => return Err(JobError::new("stopped")), // cancel, timeout or shutdown
        }

        Ok(json!({ "sent": true })) // shown as the occurrence result
    })?;

    // Runs until Ctrl+C or SIGTERM, then shuts down gracefully.
    worker.run_until_signal().await?;
    Ok(())
}
```

Then create a job in the dashboard: pick the worker `mail-worker` and the job `SendEmailJob`, and set a schedule.

## Jobs

`handle(name, handler)` and `handle_with(name, options, handler)` register an async closure under the name the dashboard dispatches it by. Names are unique within a worker and matched case-insensitively. For handlers that hold state, such as a database pool, implement the `Job` trait and register it with `register(name, options, job)`:

```rust
use milvaion_worker::{Job, JobContext, JobError, JobOptions};

struct ReportJob {
    db: sqlx::PgPool,
}

impl Job for ReportJob {
    type Output = String;

    async fn execute(&self, ctx: JobContext) -> Result<String, JobError> {
        let rows = count_rows(&self.db).await?;
        ctx.info(format!("{rows} rows"));
        Ok(format!("Report with {rows} rows"))
    }
}

worker.register("ReportJob", JobOptions::default(), ReportJob { db: pool })?;
```

The handler receives a `JobContext`:

| Member | Description |
|---|---|
| `ctx.data::<T>()` | Decodes the job data with serde. Data that does not match `T` returns a permanent error. A job without data decodes from `null`, so `Option<T>` accepts it. |
| `ctx.raw_data()` | Job data as the raw JSON string. |
| `ctx.occurrence_id()` | Id of this execution. It stays the same across retries and redeliveries, so you can use it as an idempotency key. |
| `ctx.retry_count()` | `0` on the first attempt, then `1`, `2` and so on. |
| `ctx.info` / `debug` / `warn` / `error(msg, Some(&err))` | Write to the occurrence log in the dashboard. |
| `ctx.log(level, msg, Some(json!({...})))` | Writes a log entry with structured data. |
| `ctx.cancellation_token()` / `ctx.cancelled()` / `ctx.is_cancelled()` | A `CancellationToken` cancelled when the job is cancelled from the dashboard, times out or the worker shuts down. |
| `ctx.abort_reason()` | Why the job was stopped: `Cancelled`, `TimedOut` or `WorkerStopping`. |
| `ctx.job()` | The scheduled job as the API sent it. |

How the handler ends decides the outcome:

| The handler... | Outcome |
|---|---|
| returns `Ok(value)` | `Completed`. A string is stored as-is, `()` or `None` stores `Job {name} completed successfully`, and anything else that implements `Serialize` is stored as JSON. |
| returns `Err(error)` | Retried with exponential backoff. After `max_retries`, the occurrence is `Failed` and the job goes to the dead letter queue. |
| returns `Err(JobError::permanent(..))`, or `.permanent()?` on a result | `Failed` without retrying, and the job goes to the dead letter queue. |
| panics | Treated as a returned error. |
| runs past its timeout | `TimedOut` |
| is cancelled from the dashboard | `Cancelled` |
| is still running when the worker stops | The job goes back to the queue and runs again. |

Any `std::error::Error` converts into `JobError` with `?`. The dashboard shows its type and its chain of causes. Use `JobError::new(msg)` and `JobError::permanent(msg)` for your own failures, and `.context("...")` to add detail.

When a job is stopped, the occurrence settles right away and the handler's task is dropped at its next `.await`. Work that never awaits, such as a blocking loop or `spawn_blocking`, keeps running; check `ctx.is_cancelled()` in it.

### Job options

| Field | Default | Description |
|---|---|---|
| `max_parallel_jobs` | min(10, worker `max_parallel_jobs`) | Jobs of this type that may run at once on this instance |
| `execution_timeout` | worker default (1 h) | Zero disables the timeout. A timeout set on the job in the dashboard takes precedence. |
| `max_retries` | 3 | Retries after the first failure. 0 disables them. |
| `base_retry_delay` | 5 s | Retry *n* waits `base × 2^(n-1)` |
| `data_schema` | none | JSON Schema of the job data, as a `serde_json::Value`. The dashboard builds the job data form from it. |
| `result_schema` | none | JSON Schema of the result |
| `routing_pattern` | `{workerId}.{name without "Job"}.*` | RabbitMQ routing pattern |
| `consumer_id` | `{name without "Job"}-consumer` | Shown in the dashboard |

`JobOptions::new().max_retries(5).data_schema(json!({...}))` sets them with a builder.

## Worker options

| Field | Default | Description |
|---|---|---|
| `worker_id` | required | Identifies the worker application. Use the same value on every replica. |
| `rabbitmq_url` | `amqp://guest:guest@localhost:5672/` | `amqps://` uses TLS (the default `rustls` feature) |
| `queue_type` | `QueueType::Classic` | Must match the API's `MilvaionConfig:RabbitMQ:QueueType` |
| `redis_url` | `Some("redis://localhost:6379")` | Redis only carries cancellation requests. `None` runs without it. |
| `redis_key_prefix` | `Milvaion:JobScheduler:` | Must match the API's `MilvaionConfig:Redis:KeyPrefix` |
| `max_parallel_jobs` | CPU count × 2 | Capacity reported for this instance |
| `execution_timeout` | 1 h | Default job timeout. Zero disables it. |
| `heartbeat_interval` | 30 s | |
| `job_heartbeat_interval` | 60 s | Keeps long jobs from being flagged as zombies |
| `shutdown_timeout` | 30 s | How long the worker lets running jobs finish when it is asked to stop |
| `startup_timeout` | 30 s | How long `run` waits for RabbitMQ and Redis |
| `version` | `1.0.0` | Shown in the dashboard |
| `instance_id` | `{worker_id}-{8 hex}` | Must be unique per process |

For containers, `WorkerOptions::from_env()` reads the following variables. Change fields afterwards to override them in code.

- `MILVAION_WORKER_ID`
- `MILVAION_RABBITMQ_URL`
- `MILVAION_RABBITMQ_QUEUE_TYPE`
- `MILVAION_REDIS_URL` (`false` disables Redis)
- `MILVAION_REDIS_KEY_PREFIX`
- `MILVAION_MAX_PARALLEL_JOBS`
- `MILVAION_EXECUTION_TIMEOUT_SECONDS`
- `MILVAION_HEARTBEAT_INTERVAL_SECONDS`

The SDK logs through [`tracing`](https://docs.rs/tracing). Install a subscriber, such as `tracing-subscriber`, to see its messages.

## Running the worker

`run_until_signal()` runs until Ctrl+C or, on Unix, SIGTERM. To stop it yourself, pass any future to `run`:

```rust
let (stop, stopped) = tokio::sync::oneshot::channel::<()>();
let worker = tokio::spawn(worker.run(async { let _ = stopped.await; }));
// ...
stop.send(()).ok();
worker.await??;
```

`run` returns an error if RabbitMQ or Redis cannot be reached within `startup_timeout`.

## Reliability

- **Reconnects.** If RabbitMQ goes away, the worker reconnects with backoff, then redeclares its queues and registers again. If Redis goes away, it reconnects and subscribes again.
- **At-least-once delivery.** A message is acknowledged only after its outcome has been reported, so a crash makes the job run again. Make handlers idempotent; `ctx.occurrence_id()` helps with that.
- **Graceful shutdown.** When the worker is asked to stop, it does the following in order:
  1. Stops taking new jobs.
  2. Waits up to `shutdown_timeout` for running jobs to finish.
  3. Returns the jobs that are still running to the queue.
  4. Flushes logs and tells the API the instance has left.

## How it works

The SDK implements the language-neutral [worker protocol](https://github.com/Milvasoft/milvaion/blob/master/docs/githubdocs/WORKER-PROTOCOL.md): the RabbitMQ queues and message formats the .NET SDK uses. It uses [lapin](https://crates.io/crates/lapin) for RabbitMQ and [redis](https://crates.io/crates/redis) for Redis.

Heartbeats report memory and CPU from the container's cgroup, or from `/proc` on Linux outside a container. On other systems both figures are 0.

## Development

```bash
cargo test                       # unit tests, plus the integration test when Docker is available
cargo run --example basic        # configure with MILVAION_* variables
```

The integration test starts RabbitMQ and Redis containers with Docker. To use running brokers instead, set `MILVAION_TEST_RABBITMQ_URL` and `MILVAION_TEST_REDIS_URL`.
