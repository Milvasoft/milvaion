---
id: other-language-workers
title: "Workers in Other Languages"
sidebar_label: "Node.js, Go, Python, Java, PHP, Rust & Ruby Workers"
sidebar_position: 123
description: "Run Milvaion jobs in Node.js, Go, Python, Java, PHP, Rust or Ruby with the worker SDKs, or write a worker in any language against the worker protocol."
keywords: [milvaion node.js worker, milvaion go worker, milvaion python worker, milvaion java worker, milvaion php worker, milvaion rust worker, milvaion ruby worker, polyglot job scheduler, worker protocol]
---

# Workers in Other Languages

The Milvaion scheduler runs on .NET, but workers don't have to. Besides the .NET worker SDK, Milvaion has worker SDKs for **Node.js**, **Go**, **Python**, **Java**, **PHP**, **Rust** and **Ruby**.

A worker built with one of them works like a .NET worker:

- It registers itself and appears on the dashboard.
- It receives the jobs scheduled for it.
- It streams its logs live.
- Failed jobs are retried with exponential backoff, then moved to the dead letter queue.
- Jobs can be cancelled from the dashboard and are stopped by timeouts.
- On shutdown it lets running jobs finish and returns the rest to the queue.

The Workers page shows each worker's language next to its name, so a mixed fleet stays readable at a glance. Expand a worker to see its runtime and SDK version.

![Workers written in PHP, Go, Node.js, Python and .NET on the Workers page, each with a language badge](https://raw.githubusercontent.com/Milvasoft/milvaion/master/docs/src/workers-languages.png)

All of them speak the same [worker protocol](https://github.com/Milvasoft/milvaion/blob/master/docs/githubdocs/WORKER-PROTOCOL.md). That document describes the RabbitMQ queues and message formats, so a worker in any other language can be written against it too.

| Language | Requires | Package | Source and full reference |
|---|---|---|---|
| Node.js | Node.js 20+ | npm `@milvasoft/milvaion-worker` | [sdks/node/milvaion-worker](https://github.com/Milvasoft/milvaion/tree/master/sdks/node/milvaion-worker) |
| Go | Go 1.24+ | `github.com/Milvasoft/milvaion/sdks/go/milvaion` | [sdks/go/milvaion](https://github.com/Milvasoft/milvaion/tree/master/sdks/go/milvaion) |
| Python | Python 3.10+ | PyPI `milvaion-worker` | [sdks/python/milvaion-worker](https://github.com/Milvasoft/milvaion/tree/master/sdks/python/milvaion-worker) |
| Java / Kotlin | Java 17+ | Maven Central `com.milvasoft:milvaion-worker` | [sdks/java/milvaion-worker](https://github.com/Milvasoft/milvaion/tree/master/sdks/java/milvaion-worker) |
| PHP | PHP 8.2+ | Packagist `milvasoft/milvaion-worker` | [sdks/php/milvaion-worker](https://github.com/Milvasoft/milvaion/tree/master/sdks/php/milvaion-worker) |
| Rust | Rust 1.89+, Tokio | crates.io `milvaion-worker` | [sdks/rust/milvaion-worker](https://github.com/Milvasoft/milvaion/tree/master/sdks/rust/milvaion-worker) |
| Ruby | Ruby 3.1+ | RubyGems `milvaion-worker` | [sdks/ruby/milvaion-worker](https://github.com/Milvasoft/milvaion/tree/master/sdks/ruby/milvaion-worker) |

:::tip No SDK for your language?
The [Command Worker](./27-command-worker.md) runs scripts and programs in any language as Milvaion jobs, with live output, retries, cancellation and timeouts, without an SDK.
:::

## How a Job Ends

The rules are the same in every language:

| The handler... | Outcome |
|---|---|
| returns a value | **Completed**. A string is stored as the result as-is; anything else is stored as JSON. |
| fails | **Retried** with exponential backoff (3 retries, starting at 5 s, by default). After the last retry the occurrence is **Failed** and the job goes to the dead letter queue. |
| fails with a permanent error | **Failed** without retrying, and the job goes to the dead letter queue. Use this for invalid data and rejected business rules. |
| fails with a retry-after error | **Failed** without retrying and without the dead letter queue; the requested time is reported with it. In a workflow step Milvaion reruns the step at that time (see [Retry at a Specific Time](20-workflows.md#retry-at-a-specific-time)); a standalone job ends there. |
| runs past its timeout | **TimedOut** |
| is cancelled from the dashboard | **Cancelled** |
| is still running when the worker stops | Goes **back to the queue** and runs again |

The retry-after error in each SDK: Node.js `RetryAfterError(retryAt, message)`, Python `RetryAfterError(retry_at, message)`, Go `milvaion.RetryAfter(at, message)`, Java `RetryAfterException(Instant retryAt, String message)`, PHP `RetryAfterException(\DateTimeInterface $retryAt, string $message)`, Rust `JobError::retry_after(at, message)`, Ruby `Milvaion::RetryAfterError.new(retry_at, message)`. Each SDK's README has an example.

Delivery is at least once. A job whose worker crashes runs again, so make handlers idempotent. Every SDK exposes the occurrence id, which stays the same across retries, as an idempotency key.

## Node.js

```bash
npm install @milvasoft/milvaion-worker
```

```ts
import { MilvaionWorker, PermanentJobError } from '@milvasoft/milvaion-worker';

const worker = new MilvaionWorker({
  workerId: 'mail-worker',
  rabbitmq: { url: 'amqp://guest:guest@localhost:5672' },
  redis: { url: 'redis://localhost:6379' },
});

worker.addJob<{ to: string }>('SendEmailJob', async ctx => {
  if (!ctx.data?.to)
    throw new PermanentJobError('Job data needs "to".');

  ctx.log(`Sending to ${ctx.data.to}`);
  await sendMail(ctx.data.to, { signal: ctx.signal }); // aborted on cancel, timeout or shutdown

  return { sent: true };
});

await worker.start();
process.once('SIGTERM', () => worker.stop().then(() => process.exit(0)));
```

## Go

```bash
go get github.com/Milvasoft/milvaion/sdks/go/milvaion
```

```go
worker, _ := milvaion.NewWorker(milvaion.Options{
	WorkerID:    "mail-worker",
	RabbitMQURL: "amqp://guest:guest@localhost:5672/",
	RedisURL:    "redis://localhost:6379",
})

worker.Handle("SendEmailJob", func(ctx context.Context, job *milvaion.Job) (any, error) {
	var data struct{ To string `json:"to"` }
	if err := job.Bind(&data); err != nil {
		return nil, err
	}
	if data.To == "" {
		return nil, milvaion.Permanentf(`job data needs "to"`)
	}

	job.Logf("Sending to %s", data.To)
	if err := sendMail(ctx, data.To); err != nil { // ctx is cancelled on cancel, timeout or shutdown
		return nil, err
	}
	return map[string]bool{"sent": true}, nil
})

ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
defer stop()
worker.Run(ctx) // returns after a graceful shutdown
```

## Python

```bash
pip install milvaion-worker
```

```python
from milvaion import JobContext, PermanentJobError, Worker

worker = Worker(worker_id="mail-worker",
                rabbitmq_url="amqp://guest:guest@localhost:5672/",
                redis_url="redis://localhost:6379")


@worker.job("SendEmailJob")
async def send_email(ctx: JobContext) -> dict:
    if not (ctx.data or {}).get("to"):
        raise PermanentJobError('Job data needs "to".')

    ctx.log(f"Sending to {ctx.data['to']}")
    await send_mail(ctx.data["to"])  # cancelled on cancel, timeout or shutdown

    return {"sent": True}


worker.run()  # SIGINT/SIGTERM shut down gracefully
```

A handler can be `async def` or a plain function. Plain functions run in a thread and should check `ctx.raise_if_aborted()` in long loops.

## Java

```xml
<dependency>
  <groupId>com.milvasoft</groupId>
  <artifactId>milvaion-worker</artifactId>
  <version>1.1.0</version>
</dependency>
```

With Gradle: `implementation("com.milvasoft:milvaion-worker:1.1.0")`.

```java
MilvaionWorker worker = new MilvaionWorker(new WorkerOptions()
        .workerId("mail-worker")
        .rabbitMqUri("amqp://guest:guest@localhost:5672/")
        .redisUri("redis://localhost:6379"));

worker.job("SendEmailJob", ctx -> {
    EmailData data = ctx.data(EmailData.class);

    if (data == null || data.to() == null)
        throw new PermanentJobException("Job data needs \"to\".");

    ctx.log("Sending to " + data.to());
    mailer.send(data); // the thread is interrupted on cancel, timeout or shutdown

    return Map.of("sent", true);
});

worker.run(); // SIGTERM or Ctrl+C shut down gracefully
```

## PHP

```bash
composer require milvasoft/milvaion-worker
```

```php
$worker = new Worker(new WorkerOptions(
    workerId: 'mail-worker',
    rabbitMqUrl: 'amqp://guest:guest@localhost:5672/',
    redisUrl: 'redis://localhost:6379',
));

$worker->job('SendEmailJob', function (JobContext $ctx): array {
    $data = $ctx->data() ?? [];

    if (empty($data['to'])) {
        throw new PermanentJobException('Job data needs "to".');
    }

    $ctx->log("Sending to {$data['to']}");
    $mailer->send($data);

    return ['sent' => true];
});

$worker->run(); // SIGTERM or Ctrl+C shut down gracefully
```

A PHP process runs one job at a time; run more processes for parallelism. With `ext-pcntl` (standard on Linux), each job runs in a forked child that the worker kills on cancel or timeout, so heartbeats keep going during long jobs.

## Rust

```bash
cargo add milvaion-worker
```

```rust
let mut worker = Worker::new(WorkerOptions::from_env()?)?;

worker.handle("SendEmailJob", |ctx: JobContext| async move {
    #[derive(serde::Deserialize)]
    struct Email { to: Option<String> }

    let email: Email = ctx.data()?; // data that does not match is a permanent error
    let to = email.to.ok_or_else(|| JobError::permanent("job data needs \"to\""))?;

    ctx.info(format!("Sending to {to}"));
    tokio::select! {
        sent = send_mail(&to) => sent?, // any error converts with ?, and is retried
        _ = ctx.cancelled() => return Err(JobError::new("stopped")), // cancel, timeout or shutdown
    }
    Ok(serde_json::json!({ "sent": true }))
})?;

worker.run_until_signal().await?; // Ctrl+C or SIGTERM shut down gracefully
```

Handlers are async closures, or types that implement the `Job` trait when they hold state such as a database pool. The SDK runs on Tokio and logs through `tracing`.

## Ruby

```bash
gem install milvaion-worker
```

```ruby
require "milvaion-worker"

worker = Milvaion::Worker.new(
  worker_id: "mail-worker",
  rabbitmq_url: "amqp://guest:guest@localhost:5672/",
  redis_url: "redis://localhost:6379"
)

worker.job("SendEmailJob") do |ctx|
  data = ctx.data || {}
  raise Milvaion::PermanentJobError, 'Job data needs "to".' if data["to"].to_s.empty?

  ctx.log("Sending to #{data['to']}")
  Mailer.deliver(data)

  { sent: true }
end

worker.run # SIGTERM or Ctrl+C shut down gracefully
```

You can also subclass `Milvaion::Job`, implement `perform(ctx)` and register the class with `worker.job(SendEmailJob)`.

Jobs run on a thread pool sized by `max_parallel_jobs`. Ruby cannot stop a thread safely from outside, so a cancelled or timed-out job is reported right away, but its thread keeps running until the handler returns. Check `ctx.aborted?` or call `ctx.raise_if_aborted` in long loops, and use `ctx.sleep` instead of `sleep`. For code that cannot check, the job option `interrupt_on_abort: true` raises an exception inside the handler's thread, with the same risks as Ruby's `Timeout` module.

## Configuration

Every SDK takes its options in code, or reads them from the same environment variables:

| Variable | Meaning |
|---|---|
| `MILVAION_WORKER_ID` | Worker id. Use the same value on every replica. |
| `MILVAION_RABBITMQ_URL` | RabbitMQ connection URL |
| `MILVAION_RABBITMQ_QUEUE_TYPE` | `classic` or `quorum`. Must match the API's `MilvaionConfig:RabbitMQ:QueueType`. |
| `MILVAION_REDIS_URL` | Redis URL for cancellation requests. `false` disables cancellation. |
| `MILVAION_REDIS_KEY_PREFIX` | Must match the API's `MilvaionConfig:Redis:KeyPrefix` |
| `MILVAION_MAX_PARALLEL_JOBS` | Capacity reported for the instance |
| `MILVAION_EXECUTION_TIMEOUT_SECONDS` | Default job timeout |
| `MILVAION_HEARTBEAT_INTERVAL_SECONDS` | Heartbeat interval |

### Kafka

Every SDK on this page can also run on Apache Kafka instead of RabbitMQ, when the API is set up for Kafka. Set `MILVAION_TRANSPORT=kafka` and point the worker at your cluster:

| Variable | Meaning |
|---|---|
| `MILVAION_TRANSPORT` | `rabbitmq` (default) or `kafka` |
| `MILVAION_KAFKA_BOOTSTRAP_SERVERS` | Comma separated `host:port` list. Default `localhost:9092`. |
| `MILVAION_KAFKA_TOPIC_PREFIX` | Must match the API's `MilvaionConfig:Kafka:TopicPrefix` |
| `MILVAION_KAFKA_SASL_MECHANISM`, `MILVAION_KAFKA_SASL_USERNAME`, `MILVAION_KAFKA_SASL_PASSWORD` | SASL: `plain`, `scram-sha-256` or `scram-sha-512` |
| `MILVAION_KAFKA_TLS`, `MILVAION_KAFKA_TLS_CA_FILE`, `MILVAION_KAFKA_TLS_CERT_FILE`, `MILVAION_KAFKA_TLS_KEY_FILE` | TLS and mutual TLS |
| `MILVAION_KAFKA_AUTO_CREATE_TOPICS` | `false` stops the worker from creating its topics |
| `MILVAION_KAFKA_PARTITIONS`, `MILVAION_KAFKA_REPLICATION_FACTOR` | Partitions and replication of the topics the worker creates. Default `12` and `-1` (the broker's default). |
| `MILVAION_KAFKA_MAX_IN_FLIGHT_PER_PARTITION` | Jobs of one partition that may run at the same time. Default: the instance's parallel job limit. |

In Python, PHP, Rust and Ruby the Kafka client is optional, so add it first (see [Using Kafka](34-using-kafka.md)). Cancellation still goes through Redis. Everything else about Kafka is described in [Using Kafka](34-using-kafka.md).

Per job, you can set:

- the number of parallel executions
- the timeout
- retry count and base delay
- a JSON Schema for the job data, which the dashboard turns into the job data form

The job name you register is the name you select in the dashboard when creating the job. It is matched case-insensitively.
