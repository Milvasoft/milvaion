# Milvaion Rust Worker SDK

Run [Milvaion](https://github.com/Milvasoft/milvaion) jobs in Rust. A Rust worker shows up in the dashboard like a .NET worker. The scheduler dispatches jobs to it, and the dashboard shows its live logs, status, retries, cancellation and timeouts.

```bash
cargo add milvaion-worker
cargo add tokio --features macros,rt-multi-thread
cargo add serde --features derive
```

It requires Rust 1.89 or later and the [Tokio](https://tokio.rs) runtime, and connects to the same RabbitMQ and Redis instances as your Milvaion API. Apache Kafka can replace RabbitMQ with the `kafka` feature; see [Kafka](#kafka).

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
| `transport` | `Transport::RabbitMq` | `Transport::Kafka` uses Kafka (needs the `kafka` feature), see [Kafka](#kafka) |
| `kafka` | `KafkaOptions::default()` | Kafka settings, used when `transport` is `Transport::Kafka` |
| `rabbitmq_url` | `amqp://guest:guest@localhost:5672/` | `amqps://` uses TLS (the default `rustls` feature) |
| `queue_type` | `QueueType::Classic` | Must match the API's `MilvaionConfig:RabbitMQ:QueueType` |
| `redis_url` | `Some("redis://localhost:6379")` | Redis only carries cancellation requests. `None` runs without it. |
| `redis_key_prefix` | `Milvaion:JobScheduler:` | Must match the API's `MilvaionConfig:Redis:KeyPrefix` |
| `max_parallel_jobs` | CPU count × 2 | Capacity reported for this instance |
| `execution_timeout` | 1 h | Default job timeout. Zero disables it. |
| `heartbeat_interval` | 30 s | |
| `job_heartbeat_interval` | 60 s | Keeps long jobs from being flagged as zombies |
| `shutdown_timeout` | 30 s | How long the worker lets running jobs finish when it is asked to stop |
| `startup_timeout` | 30 s | How long `run` waits for the broker and Redis |
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
- `MILVAION_TRANSPORT` (`rabbitmq` or `kafka`) and the `MILVAION_KAFKA_*` variables listed under [Kafka](#kafka)

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

`run` returns an error if the broker (RabbitMQ or Kafka) or Redis cannot be reached within `startup_timeout`.

## Kafka

RabbitMQ is the default broker. A deployment can use Apache Kafka instead. The API and every worker of a deployment must use the same broker, so switch the API to Kafka too (`MilvaionConfig:Transport:Type`). Redis still carries cancellation requests, as on RabbitMQ.

Kafka support is behind the `kafka` feature, so RabbitMQ-only workers do not compile it:

```bash
cargo add milvaion-worker --features kafka
```

The feature uses [rdkafka](https://crates.io/crates/rdkafka) and builds librdkafka from source, with a vendored OpenSSL for TLS and SASL/SCRAM, and libcurl for OAUTHBEARER/OIDC also built from source. The build needs CMake, a C compiler and Perl (Debian and Ubuntu: `apt-get install cmake build-essential perl`; the `rust` Docker images only lack `cmake`). No system librdkafka, OpenSSL or libcurl, nor their development headers, is needed. The first build takes a few minutes; later builds reuse it.

```rust
use milvaion_worker::{KafkaOptions, Transport, Worker, WorkerOptions};

let mut options = WorkerOptions::new("mail-worker");
options.transport = Transport::Kafka;
options.kafka = KafkaOptions {
    brokers: vec!["kafka-1:9092".into(), "kafka-2:9092".into()],
    topic_prefix: "prod.".into(), // must match the API's
    ..KafkaOptions::default()
};
options.redis_url = Some("redis://localhost:6379".into());

let mut worker = Worker::new(options)?;
```

Handlers do not change. Jobs, outcomes, retries, logs and cancellation behave as on RabbitMQ.

| `KafkaOptions` field | Default | Description |
|---|---|---|
| `brokers` | `["localhost:9092"]` | Bootstrap servers. An entry may be a comma separated list. |
| `tls` | `false` | Turns TLS on. Setting any of the three files below turns it on too. |
| `tls_ca_file` | system CA certificates | PEM bundle used to verify the brokers |
| `tls_cert_file`, `tls_key_file` | none | PEM client certificate and key for mutual TLS |
| `sasl_mechanism` | `None` | `KafkaSaslMechanism::Plain`, `ScramSha256` or `ScramSha512`, with `sasl_username` and `sasl_password` |
| `topic_prefix` | empty | Put in front of every topic and consumer group name. Only `a-z A-Z 0-9 . _ -`. Must match the API's. |
| `auto_create_topics` | `true` | Creates the worker's topics at startup |
| `num_partitions` | 12 | Partitions of the topics the worker creates |
| `replication_factor` | -1 | Replication of the topics the worker creates. -1 uses the broker's default. |
| `max_in_flight_per_partition` | 0 (the worker's `max_parallel_jobs`) | Jobs of one partition that may run at the same time |

`WorkerOptions::from_env()` reads them from `MILVAION_TRANSPORT` (`kafka`), `MILVAION_KAFKA_BOOTSTRAP_SERVERS` (or `KAFKA_BOOTSTRAP_SERVERS`), `MILVAION_KAFKA_TLS` (`true`), `MILVAION_KAFKA_TLS_CA_FILE`, `MILVAION_KAFKA_TLS_CERT_FILE`, `MILVAION_KAFKA_TLS_KEY_FILE`, `MILVAION_KAFKA_SASL_MECHANISM` (`plain`, `scram-sha-256` or `scram-sha-512`), `MILVAION_KAFKA_SASL_USERNAME`, `MILVAION_KAFKA_SASL_PASSWORD`, `MILVAION_KAFKA_TOPIC_PREFIX`, `MILVAION_KAFKA_AUTO_CREATE_TOPICS` (`false` disables it), `MILVAION_KAFKA_PARTITIONS` (or `MILVAION_KAFKA_NUM_PARTITIONS`), `MILVAION_KAFKA_REPLICATION_FACTOR` and `MILVAION_KAFKA_MAX_IN_FLIGHT_PER_PARTITION`. `Worker::new` returns an error for `Transport::Kafka` when the crate was built without the `kafka` feature.

How it works on Kafka:

- **Topics.** The worker reads `milvaion.jobs.{worker}` and `milvaion.jobs.{worker}.retry`, where `{worker}` is the worker id in lower case with every character outside `a-z 0-9 . _ -` replaced by `_`. It writes to `milvaion.status`, `milvaion.logs`, `milvaion.heartbeats`, `milvaion.registrations` and `milvaion.dlq`. All names get the topic prefix.
- **Instances.** Every instance of a worker joins the consumer group `milvaion.worker.{worker}` (cooperative-sticky assignment). Partitions are shared between the instances, so a topic with 12 partitions serves at most 12 instances. Raise `num_partitions` before you scale out further.
- **Parallelism.** An instance runs up to `max_parallel_jobs` jobs at once, also several from one partition, up to `max_in_flight_per_partition`. The per job `max_parallel_jobs` of `JobOptions` applies to RabbitMQ only. When the instance is full, it pauses the partitions and keeps polling, so it stays in the group.
- **Commits.** Auto commit is off. A job record is committed only after its outcome has been reported. Kafka commits a position, not a single record, so a partition is committed up to its oldest unfinished job, at most every 500 ms, when partitions are revoked and at shutdown. If an instance crashes, every record after that one runs again, including jobs that had already finished. Make handlers idempotent.
- **Retries.** A failed attempt with retries left is written to the retry topic with a time before which it must not run (`base_retry_delay × 2^n`). The instance slot is free during the wait. The instance that reads the retry holds it until it is due, and the occurrence keeps sending job heartbeats meanwhile.
- **Dead letters.** A job that fails for good goes to `milvaion.dlq` as on RabbitMQ. A record that is not a valid job is forwarded there unchanged, with the headers `x-original-topic` and `x-dlq-reason`.
- **Shutdown.** The worker stops taking records, waits up to `shutdown_timeout`, writes the jobs that are still running back to their topic, commits and leaves the group.
- **Priority** is best effort: records of a partition are read in the order the API wrote them.

The full wire contract is in [KAFKA-TRANSPORT.md](https://github.com/Milvasoft/milvaion/blob/master/docs/githubdocs/KAFKA-TRANSPORT.md).

## Reliability

- **Reconnects.** If RabbitMQ goes away, the worker reconnects with backoff, then redeclares its queues and registers again. On Kafka, librdkafka reconnects by itself and the worker registers again once heartbeats get through. If Redis goes away, it reconnects and subscribes again.
- **At-least-once delivery.** A message is acknowledged only after its outcome has been reported, so a crash makes the job run again. Make handlers idempotent; `ctx.occurrence_id()` helps with that.
- **Graceful shutdown.** When the worker is asked to stop, it does the following in order:
  1. Stops taking new jobs.
  2. Waits up to `shutdown_timeout` for running jobs to finish.
  3. Returns the jobs that are still running to the queue.
  4. Flushes logs and tells the API the instance has left.

## How it works

The SDK implements the language-neutral [worker protocol](https://github.com/Milvasoft/milvaion/blob/master/docs/githubdocs/WORKER-PROTOCOL.md): the RabbitMQ queues and message formats the .NET SDK uses. It uses [lapin](https://crates.io/crates/lapin) for RabbitMQ, [rdkafka](https://crates.io/crates/rdkafka) for Kafka (with the `kafka` feature) and [redis](https://crates.io/crates/redis) for Redis.

Heartbeats report memory and CPU from the container's cgroup, or from `/proc` on Linux outside a container. On other systems both figures are 0.

## Development

```bash
cargo test                       # unit tests, plus the integration test when Docker is available
cargo test --features kafka      # adds the Kafka unit tests, and the Kafka integration tests when MILVAION_KAFKA_BROKERS is set
cargo run --example basic        # configure with MILVAION_* variables
```

The integration test starts RabbitMQ and Redis containers with Docker. To use running brokers instead, set `MILVAION_TEST_RABBITMQ_URL` and `MILVAION_TEST_REDIS_URL`.

The Kafka integration tests (`tests/kafka.rs`) run against the broker in `MILVAION_KAFKA_BROKERS`, for example a single-node `apache/kafka` container, and are skipped without it. `MILVAION_TEST_REQUIRE_BROKERS=1` makes a missing broker a failure.
