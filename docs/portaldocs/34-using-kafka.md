---
id: using-kafka
title: "Using Kafka"
sidebar_label: "Using Kafka"
sidebar_position: 126
description: "Run Milvaion on Apache Kafka instead of RabbitMQ: settings for the API and workers, topics, retries, monitoring and deployment."
keywords: [milvaion kafka, kafka job scheduler, kafka worker, kafka transport, kraft, consumer lag, rabbitmq alternative]
---

# Using Kafka

Milvaion sends jobs to workers, and receives their status updates, logs and heartbeats, through a message broker. RabbitMQ is the default. If your company already runs Apache Kafka, Milvaion can use it instead, so you do not have to operate a second broker.

You choose the broker per deployment. The API and every worker must use the same one.

## When to use Kafka

Use Kafka when you already run it and want one broker to look after. Stay on RabbitMQ when you start from scratch: it needs less setup, and a few features work better on it:

| | RabbitMQ | Kafka |
|---|---|---|
| Routing | Wildcard routing patterns | By worker id: one topic per worker |
| Retry backoff | The worker waits, holding a slot | A delay topic; the slot is free while waiting |
| Priority | Best effort | Best effort |
| Cancellation | Redis | Redis (unchanged) |
| Worker SDKs | All | .NET, Node.js, Go and Java |
| Hangfire and Quartz adapters | Yes | Not yet |

Python, PHP, Rust and Ruby workers support RabbitMQ only for now.

## Configure the API

Set the transport and point the API at your cluster. In `appsettings.json`:

```json
{
  "MilvaionConfig": {
    "Transport": { "Type": "Kafka" },
    "Kafka": {
      "BootstrapServers": "kafka-1:9092,kafka-2:9092",
      "TopicPrefix": "",
      "NumPartitions": 12,
      "ReplicationFactor": -1
    }
  }
}
```

Or with environment variables:

```bash
MilvaionConfig__Transport__Type=Kafka
MilvaionConfig__Kafka__BootstrapServers=kafka-1:9092,kafka-2:9092
```

With Kafka selected, the RabbitMQ settings are ignored and RabbitMQ does not have to run.

### All Kafka settings

The same settings exist on the API (`MilvaionConfig:Kafka`) and on .NET workers (`Worker:Kafka`).

| Setting | Default | What it does |
|---|---|---|
| `BootstrapServers` | `localhost:9092` | Brokers to connect to, comma separated |
| `SecurityProtocol` | `Plaintext` | `Plaintext`, `Ssl`, `SaslPlaintext` or `SaslSsl` |
| `SaslMechanism` | `Plain` | `Plain`, `ScramSha256` or `ScramSha512` |
| `SaslUsername` / `SaslPassword` | empty | SASL credentials |
| `SslCaLocation` | empty | CA file for brokers with a private certificate authority |
| `SslCertificateLocation` / `SslKeyLocation` / `SslKeyPassword` | empty | Client certificate for mutual TLS |
| `TopicPrefix` | empty | Put in front of every topic and consumer group name, for example `prod.`. Use the same value everywhere. |
| `AutoCreateTopics` | `true` | Create missing topics at startup |
| `NumPartitions` | `12` | Partitions of the topics Milvaion creates |
| `ReplicationFactor` | `-1` | Replicas of the topics Milvaion creates. `-1` means the broker's default. |
| `MaxInFlightPerPartition` | worker's `MaxParallelJobs` | Workers only. How many jobs of one partition may run at the same time. |
| `LagWarningThreshold` / `LagCriticalThreshold` | `5000` / `10000` | API only. Consumer lag at which the monitoring page shows a warning or a critical state. |

A typical secured cluster:

```json
"Kafka": {
  "BootstrapServers": "kafka.example.com:9093",
  "SecurityProtocol": "SaslSsl",
  "SaslMechanism": "ScramSha512",
  "SaslUsername": "milvaion",
  "SaslPassword": "from-a-secret",
  "TopicPrefix": "prod."
}
```

## Configure a .NET worker

Set the same two things in the worker's `appsettings.json`:

```json
{
  "Worker": {
    "WorkerId": "email-worker",
    "Transport": { "Type": "Kafka" },
    "Kafka": {
      "BootstrapServers": "kafka-1:9092,kafka-2:9092"
    }
  }
}
```

Nothing changes in your job classes. A Kafka worker creates its own topics at startup, registers with the API and starts reading jobs.

Each worker id gets one topic, `milvaion.jobs.{worker-id}`. All job types of that worker share it, and the worker picks the right job class from the message, as it does on RabbitMQ. Routing patterns are still sent to the API, but on Kafka they do not decide where a job goes: the job's worker id does.

## Configure Node.js, Go and Java workers

These SDKs take a transport option or read environment variables:

```bash
MILVAION_TRANSPORT=kafka
MILVAION_KAFKA_BOOTSTRAP_SERVERS=kafka-1:9092,kafka-2:9092
MILVAION_KAFKA_TOPIC_PREFIX=prod.
```

Each SDK's README lists the remaining options, such as SASL and TLS.

## Topics

Milvaion creates these topics (shown without a prefix):

| Topic | Carries |
|---|---|
| `milvaion.jobs.{worker}` | Jobs for one worker |
| `milvaion.jobs.{worker}.retry` | Jobs waiting for their next retry |
| `milvaion.status` | Status updates |
| `milvaion.logs` | Job logs |
| `milvaion.heartbeats` | Worker heartbeats |
| `milvaion.registrations` | Worker registrations |
| `milvaion.dlq` | Jobs that failed for good (dead letter topic) |
| `milvaion.external.registrations`, `milvaion.external.occurrences` | Reserved for external scheduler adapters |

If your cluster does not let applications create topics, set `AutoCreateTopics` to `false` and create them yourself, including one job topic and one retry topic per worker id.

The number of partitions limits how many copies of a worker can share the work. A job topic with 12 partitions can keep 12 worker pods busy. Plan the partition count before you scale out.

## Retries and failed jobs

A failed job is retried the same number of times as on RabbitMQ, with the same growing delay. The difference is where it waits: the worker writes the job to the worker's retry topic with the time it may run again, and frees its slot. Any copy of the worker picks it up when it is due. A retry never runs early. It can run a little late when several retries with different delays sit in the same partition.

When the retries run out, or the job fails permanently, the job goes to `milvaion.dlq`. The API reads it from there and you find it under **Failed Occurrences**, exactly as with RabbitMQ.

## Delivery guarantees

Delivery is at least once, as on RabbitMQ. A job is only marked as read after its result has been reported. If a worker crashes, its unfinished jobs run again on another worker. On Kafka a crash can also repeat jobs that had already finished, when they sit behind a slow job in the same partition, so keep your jobs safe to run twice. Workers with offline resilience turned on skip jobs they have already finished.

## Cancellation

Cancelling a running job works the same way on both brokers: the request goes to the worker over Redis, not over Kafka.

## Monitoring

- The **health check** reports Kafka instead of RabbitMQ. It is healthy when a broker answers.
- The **Monitoring** page lists topics instead of queues. The number shown is the **consumer lag**: messages written to the topic that the reading consumer group has not committed yet. It is the Kafka equivalent of queue depth.
- The consumer count is the number of group members that read the topic. Figures that only RabbitMQ has, such as unacknowledged messages, show as not available.

## Run it locally

The repository includes a Compose file that adds a single Kafka node (KRaft mode, no ZooKeeper) and switches the API and the sample worker to it:

```bash
docker compose -f docker-compose.yml -f docker-compose.kafka.yml up -d
```

A worker running on your machine connects to `localhost:29092`.

## Kubernetes

The Helm chart does not install Kafka. Point it at your cluster:

```yaml
transport: kafka
kafka:
  bootstrapServers: kafka-0.kafka:9093,kafka-1.kafka:9093
  securityProtocol: SaslSsl
  saslMechanism: ScramSha512
  username: milvaion
  existingSecret: milvaion-kafka
  existingSecretPasswordKey: kafka-password
  topicPrefix: prod.
```

The chart passes these settings to the API and to every worker it deploys, and stops requiring RabbitMQ.

## Switching an existing installation

Switching moves no messages. Pause the dispatcher (Monitoring → Emergency stop), let the workers finish what is queued, then change the transport on the API and on every worker and restart them all. Resume the dispatcher when the workers have registered again.

## Learn more

The design and the full wire format, for anyone writing a worker in another language, are in [KAFKA-TRANSPORT.md](https://github.com/Milvasoft/milvaion/blob/master/docs/githubdocs/KAFKA-TRANSPORT.md).
