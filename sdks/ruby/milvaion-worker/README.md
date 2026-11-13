# Milvaion Ruby Worker SDK

Run [Milvaion](https://github.com/Milvasoft/milvaion) jobs in Ruby. A Ruby worker shows up in the dashboard like a .NET worker. The scheduler dispatches jobs to it, and the dashboard shows its live logs, status, retries, cancellation and timeouts.

```bash
gem install milvaion-worker
```

Or add it to your Gemfile:

```ruby
gem "milvaion-worker", "~> 1.0"
```

It requires Ruby 3.1 or later, and connects to the same RabbitMQ and Redis instances as your Milvaion API. It uses [bunny](https://github.com/ruby-amqp/bunny) for RabbitMQ and [redis-rb](https://github.com/redis/redis-rb) for Redis.

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
| runs past its timeout | `TimedOut` |
| is cancelled from the dashboard | `Cancelled` |
| is still running when the worker stops | The job goes back to the queue and runs again. |

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

For containers, `Milvaion::Worker.new(Milvaion::WorkerOptions.from_env)` reads the following variables. Keyword arguments to `from_env` take precedence.

- `MILVAION_WORKER_ID`
- `MILVAION_RABBITMQ_URL`
- `MILVAION_RABBITMQ_QUEUE_TYPE`
- `MILVAION_REDIS_URL` (`false` disables Redis)
- `MILVAION_REDIS_KEY_PREFIX`
- `MILVAION_MAX_PARALLEL_JOBS`
- `MILVAION_EXECUTION_TIMEOUT_SECONDS`
- `MILVAION_HEARTBEAT_INTERVAL_SECONDS`

## Reliability

- **Reconnects.** If RabbitMQ goes away, bunny's automatic recovery restores the connection with its queues and consumers, and the worker registers again. The Redis listener reconnects and subscribes again.
- **At-least-once delivery.** A message is acknowledged only after its outcome has been reported, so a crash makes the job run again. Make handlers idempotent; `ctx.occurrence_id` helps with that.
- **Graceful shutdown.** `stop` does the following in order:
  1. Stops taking new jobs, and returns jobs that were waiting for a free thread to the queue.
  2. Waits up to `shutdown_timeout` for running jobs to finish.
  3. Returns the jobs that are still running to the queue.
  4. Flushes logs and tells the API the instance has left.

## How it works

The SDK implements the language-neutral [worker protocol](https://github.com/Milvasoft/milvaion/blob/master/docs/githubdocs/WORKER-PROTOCOL.md): the RabbitMQ queues and message formats the .NET SDK uses.

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
