---
id: other-language-workers
title: "Workers in Other Languages"
sidebar_label: "Node.js, Go, Python, Java & PHP Workers"
sidebar_position: 123
description: "Run Milvaion jobs in Node.js, Go, Python, Java or PHP with the worker SDKs, or write a worker in any language against the worker protocol."
keywords: [milvaion node.js worker, milvaion go worker, milvaion python worker, milvaion java worker, milvaion php worker, polyglot job scheduler, worker protocol]
---

# Workers in Other Languages

The Milvaion scheduler runs on .NET, but workers don't have to. Besides the .NET worker SDK, Milvaion has worker SDKs for **Node.js**, **Go**, **Python**, **Java** and **PHP**.

A worker built with one of them works like a .NET worker:

- It registers itself and appears on the dashboard.
- It receives the jobs scheduled for it.
- It streams its logs live.
- Failed jobs are retried with exponential backoff, then moved to the dead letter queue.
- Jobs can be cancelled from the dashboard and are stopped by timeouts.
- On shutdown it lets running jobs finish and returns the rest to the queue.

All of them speak the same [worker protocol](https://github.com/Milvasoft/milvaion/blob/master/docs/githubdocs/WORKER-PROTOCOL.md). That document describes the RabbitMQ queues and message formats, so a worker in any other language can be written against it too.

| Language | Requires | Source and full reference |
|---|---|---|
| Node.js | Node.js 20+ | [sdks/node/milvaion-worker](https://github.com/Milvasoft/milvaion/tree/master/sdks/node/milvaion-worker) |
| Go | Go 1.24+ | [sdks/go/milvaion](https://github.com/Milvasoft/milvaion/tree/master/sdks/go/milvaion) |
| Python | Python 3.10+ | [sdks/python/milvaion-worker](https://github.com/Milvasoft/milvaion/tree/master/sdks/python/milvaion-worker) |
| Java / Kotlin | Java 17+ | [sdks/java/milvaion-worker](https://github.com/Milvasoft/milvaion/tree/master/sdks/java/milvaion-worker) |
| PHP | PHP 8.2+ | [sdks/php/milvaion-worker](https://github.com/Milvasoft/milvaion/tree/master/sdks/php/milvaion-worker) |

:::info
The SDKs are not published to the package registries yet. Install them from the GitHub repository.
:::

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
| runs past its timeout | **TimedOut** |
| is cancelled from the dashboard | **Cancelled** |
| is still running when the worker stops | Goes **back to the queue** and runs again |

Delivery is at least once. A job whose worker crashes runs again, so make handlers idempotent. Every SDK exposes the occurrence id, which stays the same across retries, as an idempotency key.

## Node.js

The package is not on npm yet. Clone the repository and build it from `sdks/node/milvaion-worker`.

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
pip install "git+https://github.com/Milvasoft/milvaion.git#subdirectory=sdks/python/milvaion-worker"
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

The artifact `com.milvasoft:milvaion-worker` is not on Maven Central yet; run `mvn install` in `sdks/java/milvaion-worker` to use it locally.

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

The package `milvasoft/milvaion-worker` is not on Packagist yet; add the repository folder as a Composer path or VCS repository.

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

Per job, you can set:

- the number of parallel executions
- the timeout
- retry count and base delay
- a JSON Schema for the job data, which the dashboard turns into the job data form

The job name you register is the name you select in the dashboard when creating the job. It is matched case-insensitively.
