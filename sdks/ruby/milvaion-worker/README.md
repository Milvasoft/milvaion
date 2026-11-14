# Milvaion Ruby Worker SDK

Run [Milvaion](https://github.com/Milvasoft/milvaion) jobs in Ruby. A Ruby worker shows up in the dashboard like a .NET worker. The scheduler dispatches jobs to it, and the dashboard shows its live logs, status, retries, cancellation and timeouts.

```bash
gem install milvaion-worker
```

Or add it to your Gemfile:

```ruby
gem "milvaion-worker", "~> 1.1"
```

It requires Ruby 3.1 or later, and connects to the same RabbitMQ and Redis instances as your Milvaion API. It uses [bunny](https://github.com/ruby-amqp/bunny) for RabbitMQ and [redis-rb](https://github.com/redis/redis-rb) for Redis. Apache Kafka can replace RabbitMQ; see [Kafka](#kafka).

## Quick start

```ruby
require "milvaion-worker"

worker = Milvaion::Worker.new(
  worker_id: "report-worker",
  rabbitmq_url: "amqp://guest:guest@localhost:5672/",
  redis_url: "redis://localhost:6379"
)

worker.job("BuildReportJob", max_retries: 3, execution_timeout: 600) do |ctx|
  data = ctx.data || {}
  raise Milvaion::PermanentJobError, 'Job data needs "month".' unless data["month"] # no retries, straight to the DLQ

  ctx.log("Building the report for #{data['month']}")

  rows = fetch_rows(data["month"])

  rows.each_slice(500) do |batch|
    ctx.raise_if_aborted # stop early if the job was cancelled or timed out
    write_report(batch)
  end

  { rows: rows.size } # shown as the occurrence result
end

worker.run # blocks; SIGINT/SIGTERM shut down gracefully
```

Then create a job in the dashboard: pick the worker `report-worker` and the job `BuildReportJob`, and set a schedule.

To run the worker inside your own process, call `worker.start` and `worker.stop` instead of `run`.

## Jobs

There are two ways to register a job. Either way, names are unique within a worker and matched case-insensitively.

**With a block** (or any object that responds to `call(ctx)`):

```ruby
worker.job("SendEmailJob", max_retries: 5) { |ctx| Mailer.deliver(ctx.data) }
worker.job("CleanupJob", Cleanup.method(:run))
```

**With a class.** Subclass `Milvaion::Job` and implement `perform(ctx)`. Each execution gets a new instance. The job name defaults to the class name without its namespace.

```ruby
class SendEmailJob < Milvaion::Job
  job_options max_retries: 5, data_schema: { type: "object", properties: { to: { type: "string" } } }

  def perform(ctx)
    ctx.log("Sending to #{ctx.data['to']}")
    { sent: true }
  end
end

worker.job(SendEmailJob)
worker.job(SendEmailJob, max_parallel_jobs: 2) # options here override job_options
```

Use `job_name "OtherName"` in the class to register it under a different name.

The handler receives a `Milvaion::JobContext`:

| Member | Description |
|---|---|
| `ctx.data` | Parsed job data with string keys, or `nil`. Data that is not valid JSON fails the job permanently. |
| `ctx.raw_data` | Job data as the raw JSON string. |
| `ctx.occurrence_id` | Id of this execution. It stays the same across retries and redeliveries, so you can use it as an idempotency key. |
| `ctx.retry_count` | `0` on the first attempt, then `1`, `2` and so on. |
| `ctx.aborted?`, `ctx.abort_reason`, `ctx.raise_if_aborted` | Whether the job was cancelled, timed out or interrupted by shutdown. `abort_reason` is `:cancelled`, `:timed_out`, `:shutdown` or `nil`. |
| `ctx.sleep(seconds)` | Like `Kernel#sleep`, but wakes up and raises `Milvaion::JobAborted` as soon as the job is aborted. |
| `ctx.log` / `info` / `debug` / `warn(message, data = nil)` | Write to the occurrence log in the dashboard. |
| `ctx.error(message, error = nil, data = nil)` | Write an error entry, with the exception's type and stack trace when given. |
| `ctx.job` | The scheduled job as the API sent it, a Hash with string keys. |

How the handler ends decides the outcome:

| The handler... | Outcome |
|---|---|
| returns a value | `Completed`. A string is stored as-is; anything else is stored as JSON. `nil` stores `Job {name} completed successfully`. |
| raises | Retried with exponential backoff. After `max_retries`, the occurrence is `Failed` and the job goes to the dead letter queue. |
| raises `Milvaion::PermanentJobError` | `Failed` without retrying, and the job goes to the dead letter queue. |
| raises `Milvaion::RetryAfterError` | `Failed` without retrying or dead-lettering; the status carries the requested retry time (see below). |
| runs past its timeout | `TimedOut` |
| is cancelled from the dashboard | `Cancelled` |
| is still running when the worker stops | The job goes back to the queue and runs again. |

### Retry at a specific time

When a retry now cannot succeed but a retry later will, for example a YouTube API quota that resets at
11:00 UTC, raise `Milvaion::RetryAfterError` with the time to run again. Any `Time` works; it is converted
to UTC.

```ruby
worker.job("UploadVideo") do |ctx|
  upload(ctx.data)
rescue QuotaExceeded
  now = Time.now.utc
  reset = Time.utc(now.year, now.month, now.day, 11)
  reset += 86_400 if reset <= now
  raise Milvaion::RetryAfterError.new(reset, "YouTube quota exhausted until #{reset.iso8601}")
end
```

The worker does not retry the job and does not send it to the dead letter queue. It reports the
occurrence as `Failed` with `retryAfter` set and logs a warning with the requested time.

- In a workflow step, Milvaion runs the step again at that time. The workflow's `MaxRetryAfter` setting
  caps how many times this can happen (default 10), and these retries do not count against `MaxStepRetries`.
- In a standalone job, the occurrence ends as `Failed` and is not retried.

Only the error the handler raises counts: a `RetryAfterError` that is only the `cause` of another error is
treated as an ordinary failure.

### Cancellation and timeouts in Ruby threads

Jobs run on a thread pool, so blocking code does not stall other jobs. Ruby has no safe way to stop a thread from outside: `Thread#raise` and `Thread#kill` can interrupt a job in the middle of an `ensure` block or while it holds a lock. So cancellation and timeouts are **cooperative** by default:

- When a job is cancelled or times out, the worker reports the occurrence as `Cancelled` or `TimedOut` and settles the message right away. It does not wait for the handler.
- The handler's thread keeps running until it returns. Until then it still uses CPU, memory and connections, and it is not counted in the worker's capacity any more.
- To stop early, check `ctx.aborted?` or call `ctx.raise_if_aborted` in long loops, and use `ctx.sleep` instead of `sleep`.

If a job cannot check the context, for example because it spends its time in one long blocking call, set `interrupt_on_abort: true` on the job. The worker then raises `Milvaion::JobAborted` inside the handler's thread when the job is aborted. This is the same mechanism as Ruby's `Timeout` module, with the same risks, so only use it for code that is safe to interrupt at any point.

### Job options

| Option | Default | Description |
|---|---|---|
| `max_parallel_jobs` | min(10, worker `max_parallel_jobs`) | Jobs of this type that may run at once on this instance |
| `execution_timeout` | worker default (3600 s) | Seconds. `0` disables the timeout. A timeout set on the job in the dashboard takes precedence. |
| `max_retries` | 3 | Retries after the first failure |
| `base_retry_delay` | 5 | Retry *n* waits `base × 2^(n-1)` seconds |
| `data_schema` | none | JSON Schema of the job data, as a Hash or a JSON string. The dashboard builds the job data form from it. |
| `result_schema` | none | JSON Schema of the result |
| `routing_pattern` | `{worker_id}.{name without "Job"}.*` | RabbitMQ routing pattern |
| `consumer_id` | `{name without "Job"}-consumer` | Shown in the dashboard |
| `interrupt_on_abort` | `false` | Raise `JobAborted` inside the handler thread on cancel or timeout (see above) |

## Worker options

Pass these to `Milvaion::Worker.new(...)` or `Milvaion::WorkerOptions.new(...)`:

| Option | Default | Description |
|---|---|---|
| `worker_id` | required | Identifies the worker application. Use the same value on every replica. |
| `rabbitmq_url` | `amqp://guest:guest@localhost:5672/` | |
| `queue_type` | `classic` | Must match the API's `MilvaionConfig:RabbitMQ:QueueType` |
| `redis_url` | `redis://localhost:6379` | Redis only carries cancellation requests. `nil` disables them. |
| `redis_key_prefix` | `Milvaion:JobScheduler:` | Must match the API's `MilvaionConfig:Redis:KeyPrefix` |
| `max_parallel_jobs` | CPU count × 2 | Size of the job thread pool, reported as this instance's capacity |
| `execution_timeout` | 3600 | Default job timeout in seconds |
| `heartbeat_interval` | 30 | |
| `job_heartbeat_interval` | 60 | Keeps long jobs from being flagged as zombies |
| `shutdown_timeout` | 30 | How long `stop` lets running jobs finish |
| `startup_timeout` | 30 | How long `start` waits for RabbitMQ and Redis |
| `version` | `1.0.0` | Shown in the dashboard |
| `instance_id` | `{worker_id}-{8 hex}` | Must be unique per process |
| `logger` | a `Logger` on `$stdout` at INFO | |
| `transport` | `rabbitmq` | `rabbitmq` or `kafka`, see [Kafka](#kafka) |
| `kafka` | none | Kafka settings, a Hash, see [Kafka](#kafka) |

For containers, `Milvaion::Worker.new(Milvaion::WorkerOptions.from_env)` reads the following variables. Keyword arguments to `from_env` take precedence.

- `MILVAION_WORKER_ID`
- `MILVAION_RABBITMQ_URL`
- `MILVAION_RABBITMQ_QUEUE_TYPE`
- `MILVAION_REDIS_URL` (`false` disables Redis)
- `MILVAION_REDIS_KEY_PREFIX`
- `MILVAION_MAX_PARALLEL_JOBS`
- `MILVAION_EXECUTION_TIMEOUT_SECONDS`
- `MILVAION_HEARTBEAT_INTERVAL_SECONDS`
- `MILVAION_TRANSPORT` and the `MILVAION_KAFKA_*` variables listed under [Kafka](#kafka)

## Kafka

RabbitMQ is the default broker. A deployment can use Apache Kafka instead. The API and every worker of a deployment must use the same broker, so switch the API to Kafka too (`MilvaionConfig:Transport:Type`). Redis still carries cancellation requests, as on RabbitMQ.

The Kafka client is the [rdkafka](https://github.com/karafka/rdkafka-ruby) gem (librdkafka). It is not a dependency of `milvaion-worker`, so add it to your Gemfile next to the SDK. The worker loads it only when the transport is `kafka`, and `start` raises an error that says so if it is missing.

```ruby
gem "milvaion-worker", "~> 1.1"
gem "rdkafka", ">= 0.19"
```

rdkafka ships precompiled builds for Linux (glibc and musl) and macOS; on other platforms it compiles librdkafka when installed, which needs a C toolchain. Ruby 3.1 gets rdkafka 0.22, newer Rubies get later releases.

```ruby
worker = Milvaion::Worker.new(
  worker_id: "mail-worker",
  transport: "kafka",
  kafka: {
    brokers: ["kafka-1:9092", "kafka-2:9092"],
    topic_prefix: "prod." # must match the API's
  },
  redis_url: "redis://localhost:6379"
)
```

Handlers do not change. Jobs, outcomes, retries, logs and cancellation behave as on RabbitMQ.

| `kafka:` key | Default | Description |
|---|---|---|
| `brokers` | `localhost:9092` | Bootstrap servers, an Array or a comma separated String |
| `tls` | `false` | Turns TLS on. Setting any of the three files below turns it on too. |
| `tls_ca_file` | system roots | PEM bundle used to verify the brokers |
| `tls_cert_file`, `tls_key_file` | none | PEM client certificate and key for mutual TLS |
| `sasl_mechanism` | none | `plain`, `scram-sha-256` or `scram-sha-512`, with `sasl_username` and `sasl_password` |
| `topic_prefix` | empty | Put in front of every topic and consumer group name. Only `a-z A-Z 0-9 . _ -`. Must match the API's. |
| `auto_create_topics` | `true` | Create the worker's topics, and the shared topics it writes to, at startup |
| `num_partitions` | 12 | Partitions of the topics the worker creates |
| `replication_factor` | -1 | Replication of the topics the worker creates. -1 uses the broker's default. |
| `max_in_flight_per_partition` | worker `max_parallel_jobs` | Jobs of one partition that may run at the same time |

`WorkerOptions.from_env` reads them from `MILVAION_TRANSPORT` (`kafka`), `MILVAION_KAFKA_BOOTSTRAP_SERVERS` (or `KAFKA_BOOTSTRAP_SERVERS`), `MILVAION_KAFKA_TLS` (`true`), `MILVAION_KAFKA_TLS_CA_FILE`, `MILVAION_KAFKA_TLS_CERT_FILE`, `MILVAION_KAFKA_TLS_KEY_FILE`, `MILVAION_KAFKA_SASL_MECHANISM`, `MILVAION_KAFKA_SASL_USERNAME`, `MILVAION_KAFKA_SASL_PASSWORD`, `MILVAION_KAFKA_TOPIC_PREFIX`, `MILVAION_KAFKA_AUTO_CREATE_TOPICS` (`false` disables it), `MILVAION_KAFKA_NUM_PARTITIONS` (or `MILVAION_KAFKA_PARTITIONS`), `MILVAION_KAFKA_REPLICATION_FACTOR` and `MILVAION_KAFKA_MAX_IN_FLIGHT_PER_PARTITION`. Keys of a `kafka:` Hash passed to `from_env` take precedence.

How it works on Kafka:

- **Topics.** The worker reads `milvaion.jobs.{worker}` and `milvaion.jobs.{worker}.retry`, where `{worker}` is the worker id in lower case with every character outside `a-z 0-9 . _ -` replaced by `_`. It writes to `milvaion.status`, `milvaion.logs`, `milvaion.heartbeats`, `milvaion.registrations` and `milvaion.dlq`. All names get the topic prefix. It picks the handler by the job name in the record, so routing patterns do not decide where a job goes.
- **Instances.** Every instance of a worker joins the consumer group `milvaion.worker.{worker}`. Partitions are shared between the instances, so a topic with 12 partitions serves at most 12 instances. Raise `num_partitions` before you scale out further.
- **Parallelism.** An instance runs up to `max_parallel_jobs` jobs at once, also several from one partition, up to `max_in_flight_per_partition`. The per job `max_parallel_jobs` applies to RabbitMQ only. When the instance is full, it pauses the partitions and keeps polling, so it stays in the group.
- **Commits.** A job record is committed only after its outcome has been reported. Kafka commits a position, not a single record, so a partition is committed up to its oldest unfinished job. If an instance crashes, every record after that one runs again, including jobs that had already finished. Make handlers idempotent; `ctx.occurrence_id` helps with that.
- **Retries.** A failed attempt with retries left is written to the retry topic with a time before which it must not run (`base × 2^n`). The instance slot is free during the wait. The instance that reads the retry holds it until it is due, and the occurrence keeps sending job heartbeats meanwhile.
- **Dead letters.** A job that fails for good goes to `milvaion.dlq` as on RabbitMQ. A record that is not a valid job is forwarded there unchanged, with the headers `x-original-topic` and `x-dlq-reason`.
- **Shutdown.** `stop` stops taking records, waits up to `shutdown_timeout`, writes the jobs that are still running back to their topic, commits and leaves the group.
- **Priority** is best effort: records of a partition are read in the order the API wrote them.

The full wire contract is in [KAFKA-TRANSPORT.md](https://github.com/Milvasoft/milvaion/blob/master/docs/githubdocs/KAFKA-TRANSPORT.md).

## Reliability

- **Reconnects.** If RabbitMQ goes away, bunny's automatic recovery restores the connection with its queues and consumers, and the worker registers again. The Redis listener reconnects and subscribes again.
- **At-least-once delivery.** A message is acknowledged only after its outcome has been reported, so a crash makes the job run again. Make handlers idempotent; `ctx.occurrence_id` helps with that.
- **Graceful shutdown.** `stop` does the following in order:
  1. Stops taking new jobs, and returns jobs that were waiting for a free thread to the queue.
  2. Waits up to `shutdown_timeout` for running jobs to finish.
  3. Returns the jobs that are still running to the queue.
  4. Flushes logs and tells the API the instance has left.

## How it works

The SDK implements the language-neutral [worker protocol](https://github.com/Milvasoft/milvaion/blob/master/docs/githubdocs/WORKER-PROTOCOL.md): the RabbitMQ queues and message formats the .NET SDK uses. On Kafka it follows [KAFKA-TRANSPORT.md](https://github.com/Milvasoft/milvaion/blob/master/docs/githubdocs/KAFKA-TRANSPORT.md).

## Development

```bash
bundle install
bundle exec rspec
bundle exec ruby examples/basic_worker.rb   # configure with MILVAION_* variables
gem build milvaion-worker.gemspec
```

The integration specs run a worker against a real RabbitMQ and Redis. They are skipped unless `MILVAION_TEST_RABBITMQ_URL` is set:

```bash
docker run -d --name rabbit -p 5672:5672 rabbitmq:4
docker run -d --name redis -p 6379:6379 redis:7
MILVAION_TEST_RABBITMQ_URL=amqp://guest:guest@localhost:5672 MILVAION_TEST_REDIS_URL=redis://localhost:6379 bundle exec rspec
```

The Kafka integration specs run against a real broker and are skipped unless `MILVAION_KAFKA_BROKERS` is set. The Gemfile adds rdkafka for development and tests.

```bash
docker run -d --name kafka -p 9092:9092 apache/kafka:3.9.0
MILVAION_KAFKA_BROKERS=localhost:9092 bundle exec rspec
```
