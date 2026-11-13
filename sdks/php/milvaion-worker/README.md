# Milvaion PHP Worker SDK

Run [Milvaion](https://github.com/Milvasoft/milvaion) jobs in PHP. A PHP worker shows up in the dashboard like a .NET worker. The scheduler dispatches jobs to it, and the dashboard shows its live logs, status, retries, cancellation and timeouts.

It requires PHP 8.2 or later with `ext-sockets`, and connects to the same RabbitMQ and Redis instances as your Milvaion API. No Redis extension is needed.

```bash
composer require milvasoft/milvaion-worker
```

The package is not on Packagist yet. Until it is, add the repository as a Composer `vcs` repository, or copy the folder into your project as a path repository.

## Quick start

```php
<?php

require 'vendor/autoload.php';

use Milvasoft\Milvaion\Worker\{JobContext, JobOptions, PermanentJobException, Worker, WorkerOptions};

$worker = new Worker(new WorkerOptions(
    workerId: 'mail-worker',
    rabbitMqUrl: 'amqp://guest:guest@localhost:5672/',
    redisUrl: 'redis://localhost:6379',
));

$worker->job('SendEmailJob', function (JobContext $ctx): array {
    $data = $ctx->data() ?? [];

    if (empty($data['to'])) {
        throw new PermanentJobException('Job data needs "to".'); // no retries, straight to the DLQ
    }

    $ctx->log("Sending to {$data['to']}");
    $mailer->send($data); // throw to fail the attempt and have it retried

    return ['sent' => true]; // shown as the occurrence result
}, new JobOptions(maxRetries: 3, executionTimeout: 600));

$worker->run(); // blocks; SIGTERM or Ctrl+C shuts down gracefully
```

Then create a job in the dashboard: pick the worker `mail-worker` and the job `SendEmailJob`, and set a schedule.

## How jobs run

A worker process runs **one job at a time**. To run more jobs in parallel, run more processes, for example with Supervisor `numprocs`, container replicas or a process manager.

With **`ext-pcntl` and `ext-posix`**, which are standard on Linux and in the official Docker images, each job runs in a forked child process. While the job runs, the worker:

- keeps sending heartbeats, so the dashboard never takes a long job for a dead worker;
- listens for cancellation from the dashboard;
- enforces the timeout;
- on cancel, timeout or shutdown, stops the job by killing the child.

A job therefore cannot block the worker, and memory a job leaks is freed when its process ends. A job that crashes the process, for example with a fatal error or `exit()`, is reported as a failure and retried.

Because every job runs in a fresh fork, state a job changes, such as a static cache, does not carry over to the next job. Warm caches before `run()` so every job starts from them.

**Without those extensions** (for example on Windows), jobs run inside the worker process. Cancellation and timeouts then only take effect when the job calls its context (`$ctx->isAborted()`, `$ctx->throwIfAborted()`, `$ctx->log(...)`), and heartbeats pause during long jobs. This mode suits development; use Linux in production.

## Jobs

`$worker->job($name, $handler, $options)` registers a handler under the name the dashboard dispatches it by. Names are unique within a worker and matched case-insensitively.

The handler receives a `JobContext`:

| Member | Description |
|---|---|
| `$ctx->data()` | Job data decoded to arrays, or `null`. Data that is not valid JSON fails the job permanently. |
| `$ctx->rawData()` | Job data as the raw JSON string. |
| `$ctx->occurrenceId` | Id of this execution. It stays the same across retries and redeliveries, so you can use it as an idempotency key. |
| `$ctx->retryCount` | `0` on the first attempt, then `1`, `2` and so on. |
| `$ctx->isAborted()`, `$ctx->throwIfAborted()` | Whether the job was cancelled, timed out or interrupted by shutdown. Only needed without `ext-pcntl`. |
| `$ctx->log` / `debug` / `warn` / `error($message, $throwable)` | Write to the occurrence log in the dashboard. |
| `$ctx->job` | The scheduled job as the API sent it. |

How the handler ends decides the outcome:

| The handler... | Outcome |
|---|---|
| returns a value | `Completed`. A string is stored as-is; anything else is stored as JSON. |
| throws | Retried with exponential backoff. After `maxRetries`, the occurrence is `Failed` and the job goes to the dead letter queue. |
| throws `PermanentJobException` | `Failed` without retrying, and the job goes to the dead letter queue. |
| runs past its timeout | `TimedOut` |
| is cancelled from the dashboard | `Cancelled` |
| is still running when the worker stops | The job goes back to the queue and runs again. |

### Job options

`new JobOptions(...)` takes:

| Option | Default | Description |
|---|---|---|
| `executionTimeout` | worker default (3600 s) | Seconds. `0` disables the timeout. A timeout set on the job in the dashboard takes precedence. |
| `maxRetries` | 3 | Retries after the first failure |
| `baseRetryDelay` | 5 | Retry *n* waits `base × 2^(n-1)` seconds |
| `dataSchema` | none | JSON Schema of the job data, as an array or JSON string. The dashboard builds the job data form from it. |
| `resultSchema` | none | JSON Schema of the result |
| `routingPattern` | `{workerId}.{name without "Job"}.*` | RabbitMQ routing pattern |
| `consumerId` | `{name without "Job"}-consumer` | Shown in the dashboard |

## Worker options

`new WorkerOptions(...)` takes:

| Option | Default | Description |
|---|---|---|
| `workerId` | required | Identifies the worker application. Use the same value on every process and replica. |
| `rabbitMqUrl` | `amqp://guest:guest@localhost:5672/` | |
| `queueType` | `classic` | Must match the API's `MilvaionConfig:RabbitMQ:QueueType` |
| `redisUrl` | `redis://localhost:6379` | Redis only carries cancellation requests. `null` disables them. |
| `redisKeyPrefix` | `Milvaion:JobScheduler:` | Must match the API's `MilvaionConfig:Redis:KeyPrefix` |
| `executionTimeout` | 3600 | Default job timeout in seconds |
| `heartbeatInterval` | 30 | |
| `jobHeartbeatInterval` | 60 | Keeps long jobs from being flagged as zombies |
| `shutdownTimeout` | 30 | How long a running job may finish after SIGTERM |
| `startupTimeout` | 30 | How long `run()` waits for RabbitMQ and Redis |
| `version` | `1.0.0` | Shown in the dashboard |
| `logger` | STDERR at `MILVAION_LOG_LEVEL` (default `info`) | Any PSR-3 logger |

For containers, `WorkerOptions::fromEnv()` reads the following variables. Named arguments win over them, for example `WorkerOptions::fromEnv(shutdownTimeout: 60)`.

- `MILVAION_WORKER_ID`
- `MILVAION_RABBITMQ_URL`
- `MILVAION_RABBITMQ_QUEUE_TYPE`
- `MILVAION_REDIS_URL` (`false` disables Redis)
- `MILVAION_REDIS_KEY_PREFIX`
- `MILVAION_EXECUTION_TIMEOUT_SECONDS`
- `MILVAION_HEARTBEAT_INTERVAL_SECONDS`

## Reliability

- **Reconnects.** If RabbitMQ goes away, the worker reconnects, redeclares its queues and registers again. The Redis listener reconnects on its own.
- **At-least-once delivery.** A message is acknowledged only after its outcome has been reported, so a crash makes the job run again. Make handlers idempotent; `$ctx->occurrenceId` helps with that.
- **Graceful shutdown.** On SIGTERM or SIGINT the worker does the following in order:
  1. Stops taking new jobs.
  2. Lets the running job finish for up to `shutdownTimeout`.
  3. Returns the job to the queue if it is still running.
  4. Flushes logs and tells the API the instance has left.

## How it works

The SDK implements the language-neutral [worker protocol](../../../docs/githubdocs/WORKER-PROTOCOL.md): the RabbitMQ queues and message formats the .NET SDK uses.

## Development

```bash
composer install
vendor/bin/phpunit
php examples/basic-worker.php   # configure with MILVAION_* variables
```
