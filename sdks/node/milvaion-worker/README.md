# Milvaion Node.js Worker SDK

Run [Milvaion](https://github.com/Milvasoft/milvaion) jobs in Node.js. A Node worker shows up in the dashboard like a .NET worker. The scheduler dispatches jobs to it, and the dashboard shows its live logs, status, retries, cancellation and timeouts.

```bash
npm install @milvasoft/milvaion-worker
```

It requires Node.js 20 or later, and connects to the same message broker and Redis instance as your Milvaion API. The broker is RabbitMQ by default, or Apache Kafka (see [Kafka](#kafka)).

## Quick start

```ts
import { MilvaionWorker, PermanentJobError } from '@milvasoft/milvaion-worker';

const worker = new MilvaionWorker({
  workerId: 'mail-worker',
  rabbitmq: { url: 'amqp://guest:guest@localhost:5672' },
  redis: { url: 'redis://localhost:6379' },
});

worker.addJob<{ to: string; subject: string }>('SendEmailJob', async ctx => {
  const { to, subject } = ctx.data;

  if (!to)
    throw new PermanentJobError('Job data needs "to".'); // no retries, straight to the DLQ

  ctx.log(`Sending "${subject}" to ${to}`);

  const response = await fetch('https://mail.example.com/send', {
    method: 'POST',
    body: JSON.stringify({ to, subject }),
    signal: ctx.signal, // aborted on cancel, timeout or shutdown
  });

  return { status: response.status }; // shown as the occurrence result
});

await worker.start();

process.once('SIGTERM', () => worker.stop().then(() => process.exit(0)));
```

Then create a job in the dashboard: pick the worker `mail-worker` and the job `SendEmailJob`, and set a schedule.

## Jobs

`addJob(name, handler, options?)` registers a handler under the name the dashboard dispatches it by. Names are unique within a worker and matched case-insensitively.

The handler receives a context:

| Member | Description |
|---|---|
| `ctx.data` | Parsed job data, typed by the generic parameter. Data that is not valid JSON fails the job permanently. |
| `ctx.rawData` | Job data as the raw JSON string. |
| `ctx.occurrenceId` | Id of this execution. It stays the same across retries and redeliveries, so you can use it as an idempotency key. |
| `ctx.retryCount` | `0` on the first attempt, then `1`, `2` and so on. |
| `ctx.signal` | An `AbortSignal` that is aborted on cancellation, timeout or worker shutdown. `ctx.signal.reason.kind` says which. |
| `ctx.throwIfAborted()` | Throws if the job has been aborted. Call it inside long loops. |
| `ctx.log` / `debug` / `warn` / `error(message, error?, data?)` | Write to the occurrence log in the dashboard. |
| `ctx.job` | The scheduled job as the API sent it. |

How the handler ends decides the outcome:

| The handler... | Outcome |
|---|---|
| returns a value | `Completed`. A string is stored as-is; anything else is stored as JSON. |
| throws | Retried with exponential backoff. After `maxRetries`, the occurrence is `Failed` and the job goes to the dead letter queue. |
| throws `PermanentJobError` | `Failed` without retrying, and the job goes to the dead letter queue. |
| runs past its timeout | `TimedOut` |
| is cancelled from the dashboard | `Cancelled` |
| is still running when the worker stops | The job goes back to the queue and runs again. |

When a job is aborted, the occurrence settles right away, even if the handler ignores the signal. A handler that ignores it keeps running in the background, though. Pass `ctx.signal` on to `fetch`, streams and timers so the work actually stops.

### Job options

| Option | Default | Description |
|---|---|---|
| `maxParallelJobs` | min(10, worker `maxParallelJobs`) | Jobs of this type that may run at once on this instance. RabbitMQ only. |
| `executionTimeoutSeconds` | worker default (3600) | `0` disables the timeout. A timeout set on the job in the dashboard takes precedence. |
| `maxRetries` | 3 | Retries after the first failure |
| `baseRetryDelaySeconds` | 5 | Retry *n* waits `base × 2^(n-1)` seconds |
| `dataSchema` | none | JSON Schema of the job data. The dashboard builds the job data form from it. |
| `resultSchema` | none | JSON Schema of the result |
| `routingPattern` | `{workerId}.{name without "Job"}.*` | RabbitMQ routing pattern. On Kafka it is only reported to the dashboard. |
| `consumerId` | `{name without "Job"}-consumer` | Shown in the dashboard |

`dataSchema` takes a plain JSON Schema object. With [zod](https://zod.dev), `z.toJSONSchema(schema)` produces one.

## Worker options

| Option | Default | Description |
|---|---|---|
| `workerId` | required | Identifies the worker application. Use the same value on every replica. |
| `transport` | `rabbitmq` | `rabbitmq` or `kafka`. Must be the same as the API's. |
| `rabbitmq.url`, or `host`, `port`, `username`, `password` and `vhost` | `amqp://guest:guest@localhost:5672/` | |
| `rabbitmq.queueType` | `classic` | Must match the API's `MilvaionConfig:RabbitMQ:QueueType` |
| `kafka` | see [Kafka](#kafka) | Used when `transport` is `kafka` |
| `redis` | `localhost:6379` | `{ url }` or `{ host, port, password, db }`. Pass `false` to disable cancellation. |
| `redis.keyPrefix` | `Milvaion:JobScheduler:` | Must match the API's `MilvaionConfig:Redis:KeyPrefix` |
| `maxParallelJobs` | CPU count × 2 | Capacity reported for this instance |
| `executionTimeoutSeconds` | 3600 | Default job timeout |
| `heartbeatIntervalSeconds` | 30 | |
| `jobHeartbeatIntervalSeconds` | 60 | Keeps long jobs from being flagged as zombies |
| `shutdownTimeoutSeconds` | 30 | How long `stop()` lets running jobs finish |
| `startupTimeoutSeconds` | 30 | How long `start()` waits for the broker and Redis |
| `version` | `npm_package_version` | Shown in the dashboard |
| `logger` | console, at `MILVAION_LOG_LEVEL` (default `info`) | Any object with `debug`, `info`, `warn` and `error` |

For containers, `workerOptionsFromEnv(overrides?)` reads the following variables:

- `MILVAION_WORKER_ID`
- `MILVAION_RABBITMQ_URL`
- `MILVAION_RABBITMQ_QUEUE_TYPE`
- `MILVAION_REDIS_URL` (`false` disables Redis)
- `MILVAION_REDIS_KEY_PREFIX`
- `MILVAION_MAX_PARALLEL_JOBS`
- `MILVAION_EXECUTION_TIMEOUT_SECONDS`
- `MILVAION_HEARTBEAT_INTERVAL_SECONDS`
- `MILVAION_TRANSPORT` (`rabbitmq` or `kafka`)
- The Kafka variables listed under [Kafka](#kafka)

## Reliability

- **Reconnects.** If RabbitMQ goes away, the worker reconnects with backoff, then redeclares its queues and registers again. The Kafka client reconnects and rejoins its group on its own, as does Redis.
- **At-least-once delivery.** A message is acknowledged only after its outcome has been reported, so a crash makes the job run again. Make handlers idempotent; `ctx.occurrenceId` helps with that.
- **Graceful shutdown.** `stop()` does the following in order:
  1. Stops taking new jobs.
  2. Waits up to `shutdownTimeoutSeconds` for running jobs to finish.
  3. Returns the jobs that are still running to the queue.
  4. Flushes logs and tells the API the instance has left.

## Kafka

Milvaion can run on Apache Kafka instead of RabbitMQ. The API and every worker of a deployment must use the same broker, so switch the API first (`MilvaionConfig:Transport:Type` set to `Kafka`). Then start the worker with `transport: 'kafka'`:

```ts
const worker = new MilvaionWorker({
  workerId: 'mail-worker',
  transport: 'kafka',
  kafka: { brokers: ['kafka-1:9092', 'kafka-2:9092'] },
  redis: { url: 'redis://localhost:6379' },
});
```

Jobs are written the same way on both brokers, and the outcomes are the same. Cancellation still goes through Redis.

| Option | Default | Description |
|---|---|---|
| `kafka.brokers` | `['localhost:9092']` | Bootstrap brokers as `host:port` |
| `kafka.ssl` | off | `true` for TLS with the system CAs, or Node TLS options such as `{ ca, cert, key }` |
| `kafka.sasl` | none | `{ mechanism, username, password }`, where `mechanism` is `plain`, `scram-sha-256` or `scram-sha-512` |
| `kafka.topicPrefix` | none | Prepended to every topic and consumer group, e.g. `prod.`. Must match the API's `TopicPrefix`. Letters, digits, `.`, `_` and `-` only. |
| `kafka.autoCreateTopics` | `true` | Create the worker's topics, and the shared topics it writes to, at startup |
| `kafka.numPartitions` | 12 | Partitions of the topics it creates |
| `kafka.replicationFactor` | -1 | Replication of the topics it creates. -1 uses the broker's default. |
| `kafka.maxInFlightPerPartition` | worker `maxParallelJobs` | Jobs of one partition that may run at the same time |

`workerOptionsFromEnv` reads `MILVAION_KAFKA_BOOTSTRAP_SERVERS` (comma separated; `KAFKA_BOOTSTRAP_SERVERS` also works), `MILVAION_KAFKA_TOPIC_PREFIX`, `MILVAION_KAFKA_SSL`, `MILVAION_KAFKA_SASL_MECHANISM`, `MILVAION_KAFKA_SASL_USERNAME`, `MILVAION_KAFKA_SASL_PASSWORD`, `MILVAION_KAFKA_AUTO_CREATE_TOPICS`, `MILVAION_KAFKA_NUM_PARTITIONS`, `MILVAION_KAFKA_REPLICATION_FACTOR` and `MILVAION_KAFKA_MAX_IN_FLIGHT_PER_PARTITION`.

How it differs from RabbitMQ:

- **Topics.** The worker reads one topic, `milvaion.jobs.{worker}`, where `{worker}` is the worker id in lower case with every character outside `a-z 0-9 . _ -` replaced by `_`. It picks the handler by the job name in the message, so routing patterns do not decide where a job goes.
- **Scaling.** Every instance of a worker joins the consumer group `milvaion.worker.{worker}`. Partitions are shared out between instances, so a topic with 12 partitions serves at most 12 instances. Within an instance, up to `maxParallelJobs` jobs run at once, also from the same partition. Per-job `maxParallelJobs` does not apply.
- **Retries.** A failed attempt is written to `milvaion.jobs.{worker}.retry` with the time it is due, and the instance's slot is free during the wait. Any instance runs it once it is due. A retry never runs early, but it can run late by up to the longest wait queued before it in the same partition.
- **Dead letters.** Failed jobs go to the `milvaion.dlq` topic. A message the worker cannot read at all is forwarded there unchanged.
- **Commits.** A job's record is committed only after its outcome has been reported. Kafka commits a position, not a single message, so a slow job holds back the commit of the records behind it in its partition. After a crash, those records run again even if they had finished. Make handlers idempotent; `ctx.occurrenceId` helps with that.
- **Shutdown.** `stop()` stops fetching, waits for running jobs, writes the records of interrupted jobs back to their topic, commits and leaves the group.
- **Priority.** Kafka has no message priority. Jobs of a partition run in the order the API sent them.

The full design and wire contract is in [KAFKA-TRANSPORT.md](https://github.com/Milvasoft/milvaion/blob/master/docs/githubdocs/KAFKA-TRANSPORT.md).

## How it works

The SDK implements the language-neutral [worker protocol](https://github.com/Milvasoft/milvaion/blob/master/docs/githubdocs/WORKER-PROTOCOL.md): the RabbitMQ queues and message formats the .NET SDK uses. Workers in other languages can be written against the same document. On Kafka it follows [KAFKA-TRANSPORT.md](https://github.com/Milvasoft/milvaion/blob/master/docs/githubdocs/KAFKA-TRANSPORT.md).

## Development

```bash
npm install
npm test          # unit tests, Node 22.6+ (runs TypeScript directly)
MILVAION_KAFKA_BROKERS=localhost:9092 npm test   # also runs the Kafka integration tests
npm run build     # emits dist/
node --experimental-strip-types examples/basic-worker.ts
```
