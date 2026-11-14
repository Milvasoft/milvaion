# Kafka Transport

Milvaion moves jobs, status updates, logs, heartbeats and registrations over a message broker. RabbitMQ is the default. Apache Kafka is an optional alternative, selected per deployment. This document is the design of the Kafka transport and the wire contract a worker must follow when it runs on Kafka. Everything not described here (message bodies, outcomes, retry counts, cancellation) is the same as in [WORKER-PROTOCOL.md](WORKER-PROTOCOL.md).

The API and every worker of a deployment must use the same transport. There is no bridge between the two brokers.

| Side | Setting | Values |
|---|---|---|
| API | `MilvaionConfig:Transport:Type` | `RabbitMQ` (default) or `Kafka` |
| API | `MilvaionConfig:Kafka` | Kafka connection and topic settings |
| .NET worker | `Worker:Transport:Type` | `RabbitMQ` (default) or `Kafka` |
| .NET worker | `Worker:Kafka` | Kafka connection and topic settings |
| Node, Go, Java, Python, PHP, Rust and Ruby workers | `transport` option, or `MILVAION_TRANSPORT=kafka` | see each SDK's README |

Every worker SDK supports Kafka. The Hangfire and Quartz adapters of the .NET SDK (external schedulers) publish over RabbitMQ only.

## Settings

| Setting | Default | Meaning |
|---|---|---|
| `BootstrapServers` | `localhost:9092` | Comma separated `host:port` list |
| `SecurityProtocol` | `Plaintext` | `Plaintext`, `Ssl`, `SaslPlaintext` or `SaslSsl` |
| `SaslMechanism` | `Plain` | `Plain`, `ScramSha256` or `ScramSha512` |
| `SaslUsername`, `SaslPassword` | empty | SASL credentials |
| `SslCaLocation` | empty | CA bundle used to verify the brokers |
| `SslCertificateLocation`, `SslKeyLocation`, `SslKeyPassword` | empty | Client certificate for mutual TLS |
| `TopicPrefix` | empty | Prepended to every topic and consumer group name, for example `prod.`. Only `a-z A-Z 0-9 . _ -` are allowed. |
| `AutoCreateTopics` | `true` | Create missing topics at startup with the two settings below |
| `NumPartitions` | `12` | Partitions of auto-created topics |
| `ReplicationFactor` | `-1` | Replication of auto-created topics. `-1` uses the broker's `default.replication.factor`. |
| `MaxInFlightPerPartition` | worker's `MaxParallelJobs` | Worker only: jobs of one partition that may run at the same time |

The API and the workers must use the same `TopicPrefix`.

## Topics

Names below omit the prefix. `{worker}` is the worker id in lower case with every character outside `a-z 0-9 . _ -` replaced by `_`.

| Topic | Direction | Key | Replaces |
|---|---|---|---|
| `milvaion.jobs.{worker}` | API → worker | job id | exchange `jobs.topic` and the worker's job queues |
| `milvaion.jobs.{worker}.retry` | worker → worker | job id | the in-process retry wait and republish |
| `milvaion.status` | worker → API | occurrence id | `job_status_updates_queue` |
| `milvaion.logs` | worker → API | occurrence id of the first entry | `worker_logs_queue` |
| `milvaion.heartbeats` | worker → API | instance id | `worker_heartbeat_queue` |
| `milvaion.registrations` | worker → API | instance id | `worker_registration_queue` |
| `milvaion.dlq` | worker → API | occurrence id | exchange `dlx_scheduled_jobs` and `failed_jobs_queue` |
| `milvaion.external.registrations` | adapter → API | job id | `external_job_registration_queue` |
| `milvaion.external.occurrences` | adapter → API | occurrence id | `external_job_occurrence_queue` |

The API creates the shared topics (status, logs, heartbeats, registrations, dlq, external) at startup. A worker creates its own job topic and retry topic at startup, before it registers. Creating a topic that exists is not an error.

### Routing patterns

RabbitMQ routes a job with a topic exchange: the worker binds one queue per routing pattern, such as `report-worker.sendemail.*`, and the API publishes with the key `report-worker.sendemail.job`. Kafka has no wildcard routing, so dispatch goes by worker id instead:

1. The API publishes a job to `milvaion.jobs.{job.WorkerId}`. If a job has no worker id, the API uses the first segment of its routing pattern (`report-worker` in the example above).
2. The worker consumes its one topic and picks the handler from `jobNameInWorker` in the body, exactly as on RabbitMQ.

Routing patterns are still sent at registration and stored, but on Kafka they do not decide where a job goes. A custom pattern that several workers share (for example two workers that both bind `email.*`) has no equivalent on Kafka: the job goes to the worker named on the job.

If the job topic does not exist (the worker has never started), the API treats the publish as unroutable, exactly like a RabbitMQ `basic.return`: the publish returns `false` and the dispatcher handles it as "worker offline".

The record key is the job id, so all occurrences of one job land in the same partition and are read in the order the API published them.

## Message format

The record value is the same UTF-8 JSON body as on RabbitMQ. Headers are UTF-8 strings. Numbers in headers are decimal strings, not binary integers.

Job records (`milvaion.jobs.{worker}` and its retry topic):

| Header | Meaning |
|---|---|
| `OccurrenceId` | Occurrence id. A worker reads the occurrence id from `OccurrenceId`, then `CorrelationId`. |
| `CorrelationId` | Same value as `OccurrenceId` |
| `JobType` | `jobNameInWorker` |
| `WorkerId` | Target worker id |
| `ExecuteAt` | ISO 8601 |
| `Priority` | 0 to 9 |
| `x-retry-count` | Retry attempt, absent means 0 |
| `MaxRetries` | Retry limit that applied when the job was retried |
| `x-not-before` | Retry topic only: Unix time in milliseconds before which the record must not run |

Dead letter records carry `OccurrenceId`, `CorrelationId`, `x-retry-count` and `MaxRetries` (`0` for a permanent failure), with the same JSON body as on RabbitMQ. A record the worker cannot parse is forwarded to `milvaion.dlq` unchanged, with the extra headers `x-original-topic` and `x-dlq-reason`.

## Consumer groups and commits

| Group | Members | Topics |
|---|---|---|
| `milvaion.api` | every API replica | status, logs, heartbeats, registrations, dlq, external |
| `milvaion.worker.{worker}` | every instance of one worker | `milvaion.jobs.{worker}` and `milvaion.jobs.{worker}.retry` |

The group name gets the topic prefix too. Partitions are the unit of parallelism across instances: a worker topic with 12 partitions can be shared by at most 12 instances. Within an instance, jobs run in parallel up to `MaxParallelJobs`, also from the same partition. The .NET worker also applies each job type's own `MaxParallelJobs`; a record whose job type is at its limit waits while records of other types go ahead. The Node, Go, Java, Python, PHP, Rust and Ruby SDKs apply only the instance limit on Kafka.

Consumers use `enable.auto.commit=false`, `auto.offset.reset=earliest` and the `cooperative-sticky` assignor. The Python SDK is the exception: its client (aiokafka) has no cooperative assignor, so it uses the eager `sticky` assignor and commits the finished range of every revoked partition. Because a consumer group needs one assignor that all members support, Python instances cannot share a worker id with instances written in another language. Delivery stays **at least once**:

- A worker commits a job record only after the job's outcome has been reported: the final status published, or the retry record or dead letter record produced and acknowledged by the broker.
- The API commits a status, log, heartbeat or dead letter record only after it has been written (for batched consumers, after the batch is in the database). This is the same point at which it acks on RabbitMQ.

Kafka commits an offset, not a single message, so a record can only be committed once every earlier record of its partition is done. Each consumer keeps an ordered tracker per partition: it records which offsets have finished and commits the end of the contiguous finished range. Commits happen at most every 500 ms, when partitions are revoked, and at shutdown.

### Slow jobs

A slow job does not block the jobs behind it in its partition: they are dispatched as soon as an instance slot is free, up to `MaxInFlightPerPartition` jobs per partition. When that limit is reached the partition is paused and the others keep flowing. What a slow job does hold back is the commit position of its partition. If the instance crashes, every record after the oldest unfinished one is delivered again, including jobs that had already finished. The worker's local state store (offline resilience) skips occurrences it has already finalized, and handlers should be idempotent as on RabbitMQ.

While all instance slots are busy, the consumer pauses every partition and keeps polling, so the group does not evict it for exceeding `max.poll.interval.ms`.

### Shutdown and rebalancing

On a graceful stop the worker stops taking records, waits for running jobs up to the host shutdown timeout, writes the records of interrupted jobs back to their topic (the RabbitMQ equivalent is a nack with requeue), commits and leaves the group. On a rebalance the worker commits what it has finished for the revoked partitions. A job still running on a revoked partition finishes and reports its outcome, but its offset can no longer be committed by this instance, so the new owner runs it again.

## Retries and the dead letter topic

Kafka has no nack or requeue, so a failed attempt that has retries left becomes a new record on a delay topic:

1. The worker publishes the `Retry` warning log as on RabbitMQ.
2. It produces the job, unchanged, to `milvaion.jobs.{worker}.retry` with `x-retry-count` incremented, `MaxRetries`, and `x-not-before` set to now + `BaseRetryDelaySeconds * 2^retryCount`.
3. Once the broker has acknowledged that record, it commits the original record. The instance slot is free during the backoff, unlike on RabbitMQ.
4. Any instance of the worker reads the retry topic. A record that is not due yet is held in memory until its `x-not-before` and then runs like any other job. Held records count as unfinished, so a retry partition that holds `MaxInFlightPerPartition` of them is paused until one becomes due.

The occurrence stays `Running` during the backoff, so the job heartbeat continues: the instance that produced the retry sends job heartbeats until the record is due, and the instance holding a record that is not due yet sends them while it waits.

A retry never runs early. Records that were read are released each at its own due time, but a paused retry partition is not read further, so a record behind a full set of held records can run late by at most the longest backoff in that partition. Keying by job id spreads unrelated jobs across partitions.

When no retries are left, or the failure is permanent, the worker publishes the `Failed` status and produces the dead letter record to `milvaion.dlq`, then commits. The API's failed occurrence handler consumes `milvaion.dlq` and creates the failed occurrence record exactly as it does from `failed_jobs_queue`.

## Priority

Kafka has no message priority. The API still sends the higher priority jobs of a dispatcher cycle first, and the `Priority` header carries the value, but within a partition records are read in publish order. Priority on Kafka is best effort, which is how it already behaves on RabbitMQ today because job queues are not priority queues.

## Cancellation

Cancellation does not use the broker. It stays on the Redis pub/sub channel described in [WORKER-PROTOCOL.md](WORKER-PROTOCOL.md#cancellation) and is unchanged.

## Monitoring

The dashboard's queue statistics, the health check and the MCP tools are transport aware:

- **Health:** the Kafka check asks the cluster for its metadata. It reports the number of brokers, and is unhealthy when none answer.
- **Queue statistics:** for each topic the API reports the consumer lag of the group that reads it (sum over partitions of the high watermark minus the committed offset). "Messages ready" becomes lag. RabbitMQ-only figures such as unacknowledged messages and consumer counts are reported as not available.
- **Skip policy:** the dispatcher's "is an occurrence still queued" check uses the lag of the worker's group on its job topic.

## SDK notes

| SDK | Kafka client | Optional through |
|---|---|---|
| .NET | Confluent.Kafka | built in |
| Node.js | kafkajs | built in |
| Go | franz-go | built in |
| Java | kafka-clients | built in |
| Python | aiokafka | the `kafka` extra |
| PHP | the `rdkafka` extension | `ext-rdkafka` in Composer `suggest` |
| Rust | rdkafka | the `kafka` Cargo feature |
| Ruby | rdkafka | the gem is added to the application's Gemfile |

- **Python:** aiokafka has no cooperative assignor, so the Python worker uses the eager `sticky` assignor (see [Consumer groups and commits](#consumer-groups-and-commits)).
- **PHP:** only the worker process talks to librdkafka, which is not fork safe. A job's child process reports its logs and outcome to the worker process, which produces them and commits. A PHP process runs one job at a time, so `MaxInFlightPerPartition` only limits how many records are buffered. The rdkafka extension has no admin API, so the PHP SDK creates topics with its own small CreateTopics client.

## Limits

- One transport per deployment. Switching moves no messages: drain the old broker first.
- No wildcard routing (see [Routing patterns](#routing-patterns)).
- Partitions cap the number of worker instances that share a topic. Increase `NumPartitions` before you scale out, or add partitions to the topic with Kafka's tools.
- The Hangfire and Quartz adapters are RabbitMQ only for now.
