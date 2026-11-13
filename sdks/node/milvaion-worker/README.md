# Milvaion Node.js Worker SDK

Run [Milvaion](https://github.com/Milvasoft/milvaion) jobs in Node.js. A Node worker shows up in the dashboard like a .NET worker. The scheduler dispatches jobs to it, and the dashboard shows its live logs, status, retries, cancellation and timeouts.

```bash
npm install @milvasoft/milvaion-worker
```

It requires Node.js 20 or later, and connects to the same RabbitMQ and Redis instances as your Milvaion API.

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
| `maxParallelJobs` | min(10, worker `maxParallelJobs`) | Jobs of this type that may run at once on this instance |
| `executionTimeoutSeconds` | worker default (3600) | `0` disables the timeout. A timeout set on the job in the dashboard takes precedence. |
| `maxRetries` | 3 | Retries after the first failure |
| `baseRetryDelaySeconds` | 5 | Retry *n* waits `base × 2^(n-1)` seconds |
| `dataSchema` | none | JSON Schema of the job data. The dashboard builds the job data form from it. |
| `resultSchema` | none | JSON Schema of the result |
| `routingPattern` | `{workerId}.{name without "Job"}.*` | RabbitMQ routing pattern |
| `consumerId` | `{name without "Job"}-consumer` | Shown in the dashboard |

`dataSchema` takes a plain JSON Schema object. With [zod](https://zod.dev), `z.toJSONSchema(schema)` produces one.

## Worker options

| Option | Default | Description |
|---|---|---|
| `workerId` | required | Identifies the worker application. Use the same value on every replica. |
| `rabbitmq.url`, or `host`, `port`, `username`, `password` and `vhost` | `amqp://guest:guest@localhost:5672/` | |
| `rabbitmq.queueType` | `classic` | Must match the API's `MilvaionConfig:RabbitMQ:QueueType` |
| `redis` | `localhost:6379` | `{ url }` or `{ host, port, password, db }`. Pass `false` to disable cancellation. |
| `redis.keyPrefix` | `Milvaion:JobScheduler:` | Must match the API's `MilvaionConfig:Redis:KeyPrefix` |
| `maxParallelJobs` | CPU count × 2 | Capacity reported for this instance |
| `executionTimeoutSeconds` | 3600 | Default job timeout |
| `heartbeatIntervalSeconds` | 30 | |
| `jobHeartbeatIntervalSeconds` | 60 | Keeps long jobs from being flagged as zombies |
| `shutdownTimeoutSeconds` | 30 | How long `stop()` lets running jobs finish |
| `startupTimeoutSeconds` | 30 | How long `start()` waits for RabbitMQ and Redis |
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

## Reliability

- **Reconnects.** If RabbitMQ goes away, the worker reconnects with backoff, then redeclares its queues and registers again. Redis reconnects on its own.
- **At-least-once delivery.** A message is acknowledged only after its outcome has been reported, so a crash makes the job run again. Make handlers idempotent; `ctx.occurrenceId` helps with that.
- **Graceful shutdown.** `stop()` does the following in order:
  1. Stops taking new jobs.
  2. Waits up to `shutdownTimeoutSeconds` for running jobs to finish.
  3. Returns the jobs that are still running to the queue.
  4. Flushes logs and tells the API the instance has left.

## How it works

The SDK implements the language-neutral [worker protocol](../../../docs/githubdocs/WORKER-PROTOCOL.md): the RabbitMQ queues and message formats the .NET SDK uses. Workers in other languages can be written against the same document.

## Development

```bash
npm install
npm test          # unit tests, Node 22.6+ (runs TypeScript directly)
npm run build     # emits dist/
node --experimental-strip-types examples/basic-worker.ts
```
