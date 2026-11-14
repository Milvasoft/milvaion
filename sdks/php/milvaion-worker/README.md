# Milvaion PHP Worker SDK

Run [Milvaion](https://github.com/Milvasoft/milvaion) jobs in PHP. A PHP worker shows up in the dashboard like a .NET worker. The scheduler dispatches jobs to it, and the dashboard shows its live logs, status, retries, cancellation and timeouts.

It requires PHP 8.2 or later with `ext-sockets`, and connects to the same RabbitMQ and Redis instances as your Milvaion API. No Redis extension is needed. Apache Kafka can replace RabbitMQ; see [Kafka](#kafka).

```bash
composer require milvasoft/milvaion-worker
```

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
| `transport` | `rabbitmq` | `rabbitmq` or `kafka`. See [Kafka](#kafka). |
| `kafka` | `new KafkaOptions()` | Kafka settings, used when `transport` is `kafka` |

For containers, `WorkerOptions::fromEnv()` reads the following variables. Named arguments win over them, for example `WorkerOptions::fromEnv(shutdownTimeout: 60)`.

- `MILVAION_WORKER_ID`
- `MILVAION_RABBITMQ_URL`
- `MILVAION_RABBITMQ_QUEUE_TYPE`
- `MILVAION_REDIS_URL` (`false` disables Redis)
- `MILVAION_REDIS_KEY_PREFIX`
- `MILVAION_EXECUTION_TIMEOUT_SECONDS`
- `MILVAION_HEARTBEAT_INTERVAL_SECONDS`
- `MILVAION_TRANSPORT` (`kafka` selects Kafka; the `MILVAION_KAFKA_*` variables are listed under [Kafka](#kafka))

## Reliability

- **Reconnects.** If RabbitMQ goes away, the worker reconnects, redeclares its queues and registers again. The Redis listener reconnects on its own.
- **At-least-once delivery.** A message is acknowledged only after its outcome has been reported, so a crash makes the job run again. Make handlers idempotent; `$ctx->occurrenceId` helps with that.
- **Graceful shutdown.** On SIGTERM or SIGINT the worker does the following in order:
  1. Stops taking new jobs.
  2. Lets the running job finish for up to `shutdownTimeout`.
  3. Returns the job to the queue if it is still running.
  4. Flushes logs and tells the API the instance has left.

## Kafka

Milvaion can run on Apache Kafka instead of RabbitMQ. The API and every worker of a deployment must use the same broker, so switch the API first (`MilvaionConfig:Transport:Type` set to `Kafka`).

Kafka needs the [`rdkafka`](https://github.com/arnaud-lb/php-rdkafka) extension, version 6 or later, which is built on librdkafka. It is optional, so Composer does not install it:

```bash
# Debian / Ubuntu, and the official php Docker images
apt-get install -y librdkafka-dev && pecl install rdkafka && docker-php-ext-enable rdkafka
```

Then start the worker with `transport: 'kafka'`:

```php
use Milvasoft\Milvaion\Worker\{KafkaOptions, Worker, WorkerOptions};

$worker = new Worker(new WorkerOptions(
    workerId: 'mail-worker',
    redisUrl: 'redis://localhost:6379',
    transport: 'kafka',
    kafka: new KafkaOptions(brokers: ['kafka-1:9092', 'kafka-2:9092']),
));
```

Without the extension, a Kafka worker fails at construction with an error that says so. Jobs are written the same way on both brokers, and the outcomes are the same. Cancellation still goes through Redis.

`new KafkaOptions(...)` takes:

| Option | Default | Description |
|---|---|---|
| `brokers` | `localhost:9092` | Bootstrap brokers as `host:port`, as an array or a comma separated string |
| `tls` | `false` | TLS with the system CAs. Setting any of the files below turns TLS on too. |
| `tlsCaFile` | none | PEM bundle used to verify the brokers |
| `tlsCertFile`, `tlsKeyFile` | none | PEM client certificate and key for mutual TLS, set together |
| `saslMechanism` | none | `plain`, `scram-sha-256` or `scram-sha-512` |
| `saslUsername`, `saslPassword` | none | SASL credentials |
| `topicPrefix` | none | Prepended to every topic and consumer group, e.g. `prod.`. Must match the API's `TopicPrefix`. Letters, digits, `.`, `_` and `-` only. |
| `autoCreateTopics` | `true` | Create the worker's topics, and the shared topics it writes to, at startup |
| `numPartitions` | 12 | Partitions of the topics it creates |
| `replicationFactor` | -1 | Replication of the topics it creates. -1 uses the broker's default. |
| `maxInFlightPerPartition` | 1 | Records of one partition the process takes before it pauses that partition. A PHP worker process runs one job at a time, so values above 1 only buffer records. |

`WorkerOptions::fromEnv()` with `MILVAION_TRANSPORT=kafka` reads the Kafka settings with `KafkaOptions::fromEnv()`: `MILVAION_KAFKA_BOOTSTRAP_SERVERS` (comma separated; `KAFKA_BOOTSTRAP_SERVERS` also works), `MILVAION_KAFKA_TLS` (`true`), `MILVAION_KAFKA_TLS_CA_FILE`, `MILVAION_KAFKA_TLS_CERT_FILE`, `MILVAION_KAFKA_TLS_KEY_FILE`, `MILVAION_KAFKA_SASL_MECHANISM`, `MILVAION_KAFKA_SASL_USERNAME`, `MILVAION_KAFKA_SASL_PASSWORD`, `MILVAION_KAFKA_TOPIC_PREFIX`, `MILVAION_KAFKA_AUTO_CREATE_TOPICS` (`false`), `MILVAION_KAFKA_PARTITIONS`, `MILVAION_KAFKA_REPLICATION_FACTOR` and `MILVAION_KAFKA_MAX_IN_FLIGHT_PER_PARTITION`.

How it differs from RabbitMQ:

- **Topics.** The worker reads one topic, `milvaion.jobs.{worker}`, where `{worker}` is the worker id in lower case with every character outside `a-z 0-9 . _ -` replaced by `_`. It picks the handler by the job name in the message, so routing patterns do not decide where a job goes.
- **Scaling.** Every process of a worker joins the consumer group `milvaion.worker.{worker}`. Partitions are shared out between processes, so a topic with 12 partitions serves at most 12 worker processes. Each process still runs one job at a time; while it does, it pauses its partitions and keeps polling, so the group does not drop it.
- **Retries.** A failed attempt is written to `milvaion.jobs.{worker}.retry` with the time it is due, and the process is free for other jobs during the wait. Any process of the worker runs it once it is due. A retry never runs early, but it can run late by up to the longest wait queued before it in the same partition. The occurrence's job heartbeats continue during the wait.
- **Dead letters.** Failed jobs go to the `milvaion.dlq` topic. A message the worker cannot read at all is forwarded there unchanged.
- **Commits.** A job's record is committed only after its outcome has been reported. Kafka commits a position, not a single message, so after a crash the records after the last commit run again even if they had finished. Make handlers idempotent; `$ctx->occurrenceId` helps with that.
- **Shutdown.** On SIGTERM or SIGINT the worker stops taking records, lets the running job finish for up to `shutdownTimeout`, writes the record of an interrupted job back to its topic, commits and leaves the group.
- **Priority.** Kafka has no message priority. Jobs of a partition run in the order the API sent them.
- **Forked jobs.** librdkafka is not fork safe, so only the worker process uses the Kafka client. A job process sends its logs and outcome to the worker process, which produces the records and commits. The job process ends without running destructors, even when the handler calls `exit()`. Without `ext-pcntl`, a job runs inside the worker process and the consumer is only polled when the job calls its context; a job that runs longer than librdkafka's `max.poll.interval.ms` (5 minutes) without doing so loses its partitions to another process, which runs the job again.

php-rdkafka has no admin API, so the worker creates its topics with Kafka's CreateTopics request itself, over the same TLS and SASL settings. Set `autoCreateTopics: false` if the worker's credentials may not create topics, and create `milvaion.jobs.{worker}` and `milvaion.jobs.{worker}.retry` yourself.

The full design and wire contract is in [KAFKA-TRANSPORT.md](https://github.com/Milvasoft/milvaion/blob/master/docs/githubdocs/KAFKA-TRANSPORT.md).

## How it works

The SDK implements the language-neutral [worker protocol](https://github.com/Milvasoft/milvaion/blob/master/docs/githubdocs/WORKER-PROTOCOL.md): the RabbitMQ queues and message formats the .NET SDK uses, or on Kafka the topics of the [Kafka transport](https://github.com/Milvasoft/milvaion/blob/master/docs/githubdocs/KAFKA-TRANSPORT.md).

## Development

```bash
composer install
vendor/bin/phpunit
php examples/basic-worker.php   # configure with MILVAION_* variables
```

The Kafka integration tests need `ext-rdkafka` and a broker. They run only when `MILVAION_KAFKA_BROKERS` is set; `tests/KafkaIntegrationTest.php` shows how to start a single-node broker:

```bash
MILVAION_KAFKA_BROKERS=localhost:29092 vendor/bin/phpunit --filter Kafka
```
