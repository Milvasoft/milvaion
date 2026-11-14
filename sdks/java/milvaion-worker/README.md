# Milvaion Java Worker SDK

Run [Milvaion](https://github.com/Milvasoft/milvaion) jobs on the JVM, in Java or Kotlin. A Java worker shows up in the dashboard like a .NET worker. The scheduler dispatches jobs to it, and the dashboard shows its live logs, status, retries, cancellation and timeouts.

It requires Java 17 or later, and connects to the same message broker (RabbitMQ by default, or [Kafka](#kafka)) and Redis instances as your Milvaion API.

```xml
<dependency>
  <groupId>com.milvasoft</groupId>
  <artifactId>milvaion-worker</artifactId>
  <version>1.1.0</version>
</dependency>
```

With Gradle: `implementation("com.milvasoft:milvaion-worker:1.1.0")`.

The SDK logs through SLF4J. Add a binding such as `slf4j-simple` or Logback to see its output.

## Quick start

```java
import com.milvasoft.milvaion.worker.*;
import java.util.Map;

public class MailWorker {
    record EmailData(String to, String subject) {}

    public static void main(String[] args) throws Exception {
        MilvaionWorker worker = new MilvaionWorker(new WorkerOptions()
                .workerId("mail-worker")
                .rabbitMqUri("amqp://guest:guest@localhost:5672/")
                .redisUri("redis://localhost:6379"));

        worker.job("SendEmailJob", ctx -> {
            EmailData data = ctx.data(EmailData.class); // data that does not match fails permanently

            if (data == null || data.to() == null)
                throw new PermanentJobException("Job data needs \"to\"."); // no retries, straight to the DLQ

            ctx.log("Sending \"" + data.subject() + "\" to " + data.to());
            mailer.send(data); // the thread is interrupted on cancel, timeout or shutdown

            return Map.of("sent", true); // shown as the occurrence result
        });

        worker.run(); // blocks; SIGTERM or Ctrl+C shuts down gracefully
    }
}
```

Then create a job in the dashboard: pick the worker `mail-worker` and the job `SendEmailJob`, and set a schedule.

To manage the lifecycle yourself, for example from a Spring bean, call `start()` and `close()` instead of `run()`.

## Jobs

`job(name, handler)` or `job(name, handler, new JobOptions()...)` registers a handler under the name the dashboard dispatches it by. Names are unique within a worker and matched case-insensitively.

The handler receives a `JobContext`:

| Member | Description |
|---|---|
| `ctx.data(Type.class)`, `ctx.data(new TypeReference<…>() {})`, `ctx.data()` | Job data bound to a type, or as a `JsonNode`. Data that does not match fails the job permanently. |
| `ctx.rawData()` | Job data as the raw JSON string. |
| `ctx.occurrenceId()` | Id of this execution. It stays the same across retries and redeliveries, so you can use it as an idempotency key. |
| `ctx.retryCount()` | `0` on the first attempt, then `1`, `2` and so on. |
| `ctx.isAborted()`, `ctx.abortReason()`, `ctx.throwIfAborted()` | Whether the job was cancelled, timed out or interrupted by shutdown. |
| `ctx.log` / `debug` / `warn` / `error(message, throwable)` | Write to the occurrence log in the dashboard. |
| `ctx.job()` | The scheduled job as the API sent it. |

How the handler ends decides the outcome:

| The handler... | Outcome |
|---|---|
| returns a value | `Completed`. A `String` is stored as-is; anything else is stored as JSON. |
| throws | Retried with exponential backoff. After `maxRetries`, the occurrence is `Failed` and the job goes to the dead letter queue. |
| throws `PermanentJobException` | `Failed` without retrying, and the job goes to the dead letter queue. |
| runs past its timeout | `TimedOut` |
| is cancelled from the dashboard | `Cancelled` |
| is still running when the worker stops | The job goes back to the queue and runs again. |

When a job is aborted, its thread is interrupted and the occurrence settles right away. Blocking calls such as `Thread.sleep` and most I/O react to the interrupt. CPU-bound loops should check `ctx.throwIfAborted()`.

### Job options

| Option | Default | Description |
|---|---|---|
| `maxParallelJobs` | min(10, worker `maxParallelJobs`) | Jobs of this type that may run at once on this instance (RabbitMQ only; on Kafka the worker-wide limit applies) |
| `executionTimeout` | worker default (1h) | `Duration.ZERO` disables the timeout. A timeout set on the job in the dashboard takes precedence. |
| `maxRetries` | 3 | Retries after the first failure |
| `baseRetryDelay` | 5s | Retry *n* waits `base × 2^(n-1)` |
| `dataSchema` | none | JSON Schema of the job data, as a JSON string, `Map` or any Jackson-serializable value. The dashboard builds the job data form from it. |
| `resultSchema` | none | JSON Schema of the result |
| `routingPattern` | `{workerId}.{name without "Job"}.*` | RabbitMQ routing pattern. Sent at registration on Kafka too, but there it does not decide where a job goes. |
| `consumerId` | `{name without "Job"}-consumer` | Shown in the dashboard |

## Worker options

| Option | Default | Description |
|---|---|---|
| `workerId` | required | Identifies the worker application. Use the same value on every replica. |
| `transport` | `TransportType.RABBITMQ` | `TransportType.KAFKA` selects [Kafka](#kafka). Must match the API. |
| `kafka` | `new KafkaOptions()` | Kafka settings, see [Kafka](#kafka) |
| `rabbitMqUri` | `amqp://guest:guest@localhost:5672/` | |
| `queueType` | `classic` | Must match the API's `MilvaionConfig:RabbitMQ:QueueType` |
| `redisUri` | `redis://localhost:6379` | Redis only carries cancellation requests. `null` disables them. |
| `redisKeyPrefix` | `Milvaion:JobScheduler:` | Must match the API's `MilvaionConfig:Redis:KeyPrefix` |
| `maxParallelJobs` | processors × 2 | Capacity reported for this instance |
| `executionTimeout` | 1h | Default job timeout |
| `heartbeatInterval` | 30s | |
| `jobHeartbeatInterval` | 60s | Keeps long jobs from being flagged as zombies |
| `shutdownTimeout` | 30s | How long `close()` lets running jobs finish |
| `startupTimeout` | 30s | How long `start()` waits for the broker and Redis |
| `version` | `1.0.0` | Shown in the dashboard |

For containers, `WorkerOptions.fromEnv()` reads the following variables:

- `MILVAION_WORKER_ID`
- `MILVAION_RABBITMQ_URL`
- `MILVAION_RABBITMQ_QUEUE_TYPE`
- `MILVAION_REDIS_URL` (`false` disables Redis)
- `MILVAION_REDIS_KEY_PREFIX`
- `MILVAION_MAX_PARALLEL_JOBS`
- `MILVAION_EXECUTION_TIMEOUT_SECONDS`
- `MILVAION_HEARTBEAT_INTERVAL_SECONDS`
- `MILVAION_TRANSPORT` (`rabbitmq` or `kafka`)
- the `MILVAION_KAFKA_*` variables listed under [Kafka](#kafka)

## Kafka

A deployment can use Apache Kafka instead of RabbitMQ. The API and every worker must use the same transport, and the same topic prefix. Job handlers do not change: the outcomes, retries, timeouts and cancellation work the same way. Cancellation still goes over Redis.

```java
MilvaionWorker worker = new MilvaionWorker(new WorkerOptions()
        .workerId("mail-worker")
        .transport(TransportType.KAFKA)
        .kafka(new KafkaOptions()
                .bootstrapServers("kafka-1:9092,kafka-2:9092")
                .topicPrefix("prod."))
        .redisUri("redis://localhost:6379"));
```

| Option | Default | Environment variable | Description |
|---|---|---|---|
| `bootstrapServers` | `localhost:9092` | `MILVAION_KAFKA_BOOTSTRAP_SERVERS` (or `KAFKA_BOOTSTRAP_SERVERS`) | Comma separated `host:port` list |
| `securityProtocol` | `PLAINTEXT` | `MILVAION_KAFKA_SECURITY_PROTOCOL` | `PLAINTEXT`, `SSL`, `SASL_PLAINTEXT` or `SASL_SSL` |
| `saslMechanism` | `PLAIN` | `MILVAION_KAFKA_SASL_MECHANISM` | `PLAIN`, `SCRAM-SHA-256` or `SCRAM-SHA-512` |
| `saslUsername`, `saslPassword` | none | `MILVAION_KAFKA_SASL_USERNAME`, `MILVAION_KAFKA_SASL_PASSWORD` | SASL credentials |
| `sslTruststoreLocation`, `sslTruststorePassword`, `sslTruststoreType` | none | `MILVAION_KAFKA_SSL_TRUSTSTORE_LOCATION`, `MILVAION_KAFKA_SSL_TRUSTSTORE_PASSWORD`, `MILVAION_KAFKA_SSL_TRUSTSTORE_TYPE` | Trust store that verifies the brokers. For a PEM CA bundle, set the type to `PEM`. |
| `sslKeystoreLocation`, `sslKeystorePassword`, `sslKeystoreType`, `sslKeyPassword` | none | `MILVAION_KAFKA_SSL_KEYSTORE_LOCATION`, `MILVAION_KAFKA_SSL_KEYSTORE_PASSWORD`, `MILVAION_KAFKA_SSL_KEYSTORE_TYPE`, `MILVAION_KAFKA_SSL_KEY_PASSWORD` | Client certificate for mutual TLS |
| `topicPrefix` | empty | `MILVAION_KAFKA_TOPIC_PREFIX` | Prepended to every topic and consumer group name, for example `prod.`. Only `a-z A-Z 0-9 . _ -`. Must match the API. |
| `autoCreateTopics` | `true` | `MILVAION_KAFKA_AUTO_CREATE_TOPICS` | Create missing topics at startup |
| `numPartitions` | `12` | `MILVAION_KAFKA_NUM_PARTITIONS` | Partitions of the topics the worker creates |
| `replicationFactor` | `-1` | `MILVAION_KAFKA_REPLICATION_FACTOR` | Replication of the topics the worker creates. `-1` uses the broker default. |
| `maxInFlightPerPartition` | worker `maxParallelJobs` | `MILVAION_KAFKA_MAX_IN_FLIGHT_PER_PARTITION` | Jobs of one partition that may run at the same time |

`KafkaOptions.fromEnv()` reads only the Kafka variables. `WorkerOptions.fromEnv()` reads them too, together with `MILVAION_TRANSPORT=kafka`.

How it works:

- **Topics.** The worker reads its own topic `milvaion.jobs.{worker}` and its retry topic `milvaion.jobs.{worker}.retry`. `{worker}` is the worker id in lower case, with every character other than `a-z 0-9 . _ -` replaced by `_`. The worker writes to `milvaion.status`, `milvaion.logs`, `milvaion.heartbeats`, `milvaion.registrations` and `milvaion.dlq`. With `autoCreateTopics`, it creates all of these at startup if they are missing.
- **Scaling.** All instances of a worker share the consumer group `milvaion.worker.{worker}`. Partitions are the unit of parallelism across instances, so a job topic with 12 partitions can be shared by at most 12 instances. Increase `numPartitions` before you scale out further.
- **Parallelism.** Within an instance, jobs run in parallel up to the worker's `maxParallelJobs`, also several from the same partition (up to `maxInFlightPerPartition`). The per-job `maxParallelJobs` option applies to RabbitMQ only.
- **Commits.** A job record is committed only after its outcome has been reported. Kafka commits a position, so a slow job holds back the commit position of its partition but not the jobs behind it. If the instance crashes, records after the oldest unfinished one run again, so handlers should be idempotent, as on RabbitMQ.
- **Retries.** A failed attempt with retries left is written to the retry topic with the time it becomes due. The instance slot is free during the backoff, and the occurrence keeps sending heartbeats until the retry runs. A retry never runs early. It can run late when a retry with a longer backoff is ahead of it in the same partition.
- **Dead letters.** When the retries are used up, or the failure is permanent, the worker reports `Failed` and writes the job to `milvaion.dlq`. A record the worker cannot read is forwarded to `milvaion.dlq` unchanged.
- **Shutdown.** `close()` stops reading, waits for running jobs up to `shutdownTimeout`, writes interrupted jobs back to their topic so they run again, commits and leaves the group.
- **Priority.** Kafka has no message priority. Jobs of a partition run in the order the API sent them.

The Kafka client (`org.apache.kafka:kafka-clients`) is a dependency of the SDK, but it is only loaded when the transport is Kafka. If you use RabbitMQ only, you can exclude it in your build.

The full wire contract is in [KAFKA-TRANSPORT.md](https://github.com/Milvasoft/milvaion/blob/master/docs/githubdocs/KAFKA-TRANSPORT.md).

## Reliability

- **Reconnects.** On RabbitMQ, the client restores the connection, queues and consumers after an outage, and the worker registers again. The Redis listener reconnects and resubscribes.
- **At-least-once delivery.** A message is acknowledged only after its outcome has been reported, so a crash makes the job run again. Make handlers idempotent; `ctx.occurrenceId()` helps with that.
- **Graceful shutdown.** `close()` does the following in order:
  1. Stops taking new jobs.
  2. Waits up to `shutdownTimeout` for running jobs to finish.
  3. Returns the jobs that are still running to the queue.
  4. Flushes logs and tells the API the instance has left.

## How it works

The SDK implements the language-neutral [worker protocol](https://github.com/Milvasoft/milvaion/blob/master/docs/githubdocs/WORKER-PROTOCOL.md): the RabbitMQ queues and message formats the .NET SDK uses, and the [Kafka transport](https://github.com/Milvasoft/milvaion/blob/master/docs/githubdocs/KAFKA-TRANSPORT.md).

## Development

```bash
mvn test
# Kafka integration tests run against a real broker and are skipped unless this is set:
MILVAION_KAFKA_BROKERS=localhost:9092 mvn test
mvn -q package dependency:copy-dependencies -DincludeScope=runtime -DoutputDirectory=target/lib
java -cp "target/milvaion-worker-1.0.0.jar:target/lib/*" examples/BasicWorker.java   # configure with MILVAION_* variables
```
