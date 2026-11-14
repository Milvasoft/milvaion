# Milvaion Python Worker SDK

Run [Milvaion](https://github.com/Milvasoft/milvaion) jobs in Python. A Python worker shows up in the dashboard like a .NET worker. The scheduler dispatches jobs to it, and the dashboard shows its live logs, status, retries, cancellation and timeouts.

```bash
pip install milvaion-worker
```

It requires Python 3.10 or later, and connects to the same broker (RabbitMQ, or [Kafka](#kafka)) and Redis instances as your Milvaion API.

## Quick start

```python
from milvaion import JobContext, PermanentJobError, Worker

worker = Worker(
    worker_id="report-worker",
    rabbitmq_url="amqp://guest:guest@localhost:5672/",
    redis_url="redis://localhost:6379",
)


@worker.job("BuildReportJob", max_retries=3, execution_timeout=600)
async def build_report(ctx: JobContext) -> dict:
    data = ctx.data or {}

    if "month" not in data:
        raise PermanentJobError('Job data needs "month".')  # no retries, straight to the DLQ

    ctx.log(f"Building the report for {data['month']}")
    rows = await fetch_rows(data["month"])  # cancelled if the job is cancelled or times out

    return {"rows": len(rows)}  # shown as the occurrence result


@worker.job("CleanupJob")
def cleanup(ctx: JobContext) -> None:  # plain functions run in a thread
    for path in old_files():
        ctx.raise_if_aborted()  # threads cannot be stopped from outside; check in long loops
        path.unlink()


if __name__ == "__main__":
    worker.run()  # blocks; SIGINT/SIGTERM shut down gracefully
```

Then create a job in the dashboard: pick the worker `report-worker` and the job `BuildReportJob`, and set a schedule.

To run the worker inside your own event loop, call `await worker.start()` and `await worker.stop()` instead of `run()`.

## Jobs

`@worker.job(name, **options)` or `worker.add_job(name, handler, JobOptions(...))` registers a handler under the name the dashboard dispatches it by. Names are unique within a worker and matched case-insensitively.

Handlers can be `async def` or plain functions. A plain function runs in a thread, so blocking code does not stall other jobs.

The handler receives a `JobContext`:

| Member | Description |
|---|---|
| `ctx.data` | Parsed job data, or `None`. Data that is not valid JSON fails the job permanently. |
| `ctx.raw_data` | Job data as the raw JSON string. |
| `ctx.occurrence_id` | Id of this execution. It stays the same across retries and redeliveries, so you can use it as an idempotency key. |
| `ctx.retry_count` | `0` on the first attempt, then `1`, `2` and so on. |
| `ctx.is_aborted`, `ctx.abort_reason`, `ctx.raise_if_aborted()` | Whether the job was cancelled, timed out or interrupted by shutdown. |
| `ctx.log` / `debug` / `warn` / `error(message, error=None, data=None)` | Write to the occurrence log in the dashboard. |
| `ctx.job` | The scheduled job as the API sent it. |

How the handler ends decides the outcome:

| The handler... | Outcome |
|---|---|
| returns a value | `Completed`. A string is stored as-is; anything else is stored as JSON. |
| raises | Retried with exponential backoff. After `max_retries`, the occurrence is `Failed` and the job goes to the dead letter queue. |
| raises `PermanentJobError` | `Failed` without retrying, and the job goes to the dead letter queue. |
| runs past its timeout | `TimedOut` |
| is cancelled from the dashboard | `Cancelled` |
| is still running when the worker stops | The job goes back to the queue and runs again. |

When a job is aborted, an `async` handler is cancelled, so it gets `asyncio.CancelledError` at its next `await`. A plain function cannot be stopped from outside. The occurrence still settles right away, but the thread keeps running until the function returns, so check `ctx.raise_if_aborted()` in long loops.

### Job options

| Option | Default | Description |
|---|---|---|
| `max_parallel_jobs` | min(10, worker `max_parallel_jobs`) | Jobs of this type that may run at once on this instance. RabbitMQ only. |
| `execution_timeout` | worker default (3600 s) | Seconds. `0` disables the timeout. A timeout set on the job in the dashboard takes precedence. |
| `max_retries` | 3 | Retries after the first failure |
| `base_retry_delay` | 5 | Retry *n* waits `base × 2^(n-1)` seconds |
| `data_schema` | none | JSON Schema of the job data, as a dict or string. The dashboard builds the job data form from it; with Pydantic, use `Model.model_json_schema()`. |
| `result_schema` | none | JSON Schema of the result |
| `routing_pattern` | `{worker_id}.{name without "Job"}.*` | RabbitMQ routing pattern |
| `consumer_id` | `{name without "Job"}-consumer` | Shown in the dashboard |

## Worker options

Pass these to `Worker(...)` or `WorkerOptions(...)`:

| Option | Default | Description |
|---|---|---|
| `worker_id` | required | Identifies the worker application. Use the same value on every replica. |
| `rabbitmq_url` | `amqp://guest:guest@localhost:5672/` | |
| `queue_type` | `classic` | Must match the API's `MilvaionConfig:RabbitMQ:QueueType` |
| `redis_url` | `redis://localhost:6379` | Redis only carries cancellation requests. `None` disables them. |
| `redis_key_prefix` | `Milvaion:JobScheduler:` | Must match the API's `MilvaionConfig:Redis:KeyPrefix` |
| `max_parallel_jobs` | CPU count × 2 | Capacity reported for this instance |
| `execution_timeout` | 3600 | Default job timeout in seconds |
| `heartbeat_interval` | 30 | |
| `job_heartbeat_interval` | 60 | Keeps long jobs from being flagged as zombies |
| `shutdown_timeout` | 30 | How long `stop()` lets running jobs finish |
| `startup_timeout` | 30 | How long `start()` waits for the broker and Redis |
| `version` | `1.0.0` | Shown in the dashboard |
| `logger` | `logging.getLogger("milvaion")` | |
| `transport` | `rabbitmq` | `rabbitmq` or `kafka`. Must match the API. |
| `kafka` | see [Kafka](#kafka) | `KafkaOptions`, used when `transport` is `kafka` |

For containers, `Worker(WorkerOptions.from_env())` reads the following variables:

- `MILVAION_WORKER_ID`
- `MILVAION_RABBITMQ_URL`
- `MILVAION_RABBITMQ_QUEUE_TYPE`
- `MILVAION_REDIS_URL` (`false` disables Redis)
- `MILVAION_REDIS_KEY_PREFIX`
- `MILVAION_MAX_PARALLEL_JOBS`
- `MILVAION_EXECUTION_TIMEOUT_SECONDS`
- `MILVAION_HEARTBEAT_INTERVAL_SECONDS`
- `MILVAION_TRANSPORT` and the Kafka variables listed under [Kafka](#kafka)

## Kafka

RabbitMQ is the default broker. A deployment can use Apache Kafka instead. The API and every worker of a deployment must use the same broker, so switch the API to Kafka too (`MilvaionConfig:Transport:Type`). Redis still carries cancellation requests, as on RabbitMQ.

The Kafka client ([aiokafka](https://github.com/aio-libs/aiokafka)) is an optional extra, so RabbitMQ workers do not install it:

```bash
pip install "milvaion-worker[kafka]"
```

```python
from milvaion import KafkaOptions, Worker

worker = Worker(
    worker_id="mail-worker",
    transport="kafka",
    kafka=KafkaOptions(
        brokers=["kafka-1:9092", "kafka-2:9092"],
        topic_prefix="prod.",  # must match the API's
    ),
    redis_url="redis://localhost:6379",
)
```

Handlers do not change. Jobs, outcomes, retries, logs and cancellation behave as on RabbitMQ.

| `KafkaOptions` field | Default | Description |
|---|---|---|
| `brokers` | `localhost:9092` | Bootstrap servers, as a list or a string. An entry may be a comma separated list. |
| `tls` | `False` | Turns TLS on. Setting any of the three files below turns it on too. |
| `tls_ca_file` | system roots | PEM bundle used to verify the brokers |
| `tls_cert_file`, `tls_key_file` | none | PEM client certificate and key for mutual TLS |
| `sasl_mechanism` | none | `plain`, `scram-sha-256` or `scram-sha-512`, with `sasl_username` and `sasl_password` |
| `topic_prefix` | empty | Put in front of every topic and consumer group name. Only `a-z A-Z 0-9 . _ -`. Must match the API's. |
| `auto_create_topics` | `True` | Create the worker's topics, and the shared topics it writes to, at startup |
| `num_partitions` | 12 | Partitions of the topics the worker creates |
| `replication_factor` | -1 | Replication of the topics the worker creates. -1 uses the broker's default. |
| `max_in_flight_per_partition` | worker `max_parallel_jobs` | Jobs of one partition that may run at the same time |

`WorkerOptions.from_env()` reads them from `MILVAION_TRANSPORT` (`kafka`), `MILVAION_KAFKA_BOOTSTRAP_SERVERS` (or `KAFKA_BOOTSTRAP_SERVERS`), `MILVAION_KAFKA_TLS` (`true`), `MILVAION_KAFKA_TLS_CA_FILE`, `MILVAION_KAFKA_TLS_CERT_FILE`, `MILVAION_KAFKA_TLS_KEY_FILE`, `MILVAION_KAFKA_SASL_MECHANISM`, `MILVAION_KAFKA_SASL_USERNAME`, `MILVAION_KAFKA_SASL_PASSWORD`, `MILVAION_KAFKA_TOPIC_PREFIX`, `MILVAION_KAFKA_AUTO_CREATE_TOPICS` (`false` disables it), `MILVAION_KAFKA_NUM_PARTITIONS` (or `MILVAION_KAFKA_PARTITIONS`), `MILVAION_KAFKA_REPLICATION_FACTOR` and `MILVAION_KAFKA_MAX_IN_FLIGHT_PER_PARTITION`. A `kafka=` argument replaces the Kafka settings of the environment.

How it works on Kafka:

- **Topics.** The worker reads `milvaion.jobs.{worker}` and `milvaion.jobs.{worker}.retry`, where `{worker}` is the worker id in lower case with every character outside `a-z 0-9 . _ -` replaced by `_`. It writes to `milvaion.status`, `milvaion.logs`, `milvaion.heartbeats`, `milvaion.registrations` and `milvaion.dlq`. All names get the topic prefix.
- **Instances.** Every instance of a worker joins the consumer group `milvaion.worker.{worker}`. Partitions are shared between the instances, so a topic with 12 partitions serves at most 12 instances. Raise `num_partitions` before you scale out further.
- **Parallelism.** An instance runs up to `max_parallel_jobs` jobs at once, also several from one partition, up to `max_in_flight_per_partition`. The per job `max_parallel_jobs` of `JobOptions` applies to RabbitMQ only. When the instance is full, it pauses the partitions and keeps polling, so it stays in the group.
- **Commits.** A job record is committed only after its outcome has been reported. Kafka commits a position, not a single record, so a partition is committed up to its oldest unfinished job. If an instance crashes, every record after that one runs again, including jobs that had already finished. Make handlers idempotent.
- **Retries.** A failed attempt with retries left is written to the retry topic with a time before which it must not run (`base × 2^n`). The instance slot is free during the wait. The instance that reads the retry holds it until it is due, and the occurrence keeps sending job heartbeats meanwhile.
- **Dead letters.** A job that fails for good goes to `milvaion.dlq` as on RabbitMQ. A record that is not a valid job is forwarded there unchanged, with the headers `x-original-topic` and `x-dlq-reason`.
- **Rebalancing.** aiokafka has no `cooperative-sticky` assignor, so the Python worker uses the eager `sticky` assignor: on a rebalance it commits what has finished and gives up its partitions, and a partition that comes straight back keeps its running jobs and continues after the records it had already taken. Do not mix Python instances with Node, Go, Java or .NET instances of the same worker id, because their consumer group would have no assignor in common.
- **Shutdown.** The worker stops taking records, waits up to `shutdown_timeout`, writes the jobs that are still running back to their topic, commits and leaves the group.
- **Priority** is best effort: records of a partition are read in the order the API wrote them.

The full wire contract is in [KAFKA-TRANSPORT.md](https://github.com/Milvasoft/milvaion/blob/master/docs/githubdocs/KAFKA-TRANSPORT.md).

## Reliability

- **Reconnects.** If RabbitMQ goes away, the connection is restored with its queues and consumers, and the worker registers again. The Redis listener reconnects and resubscribes.
- **At-least-once delivery.** A message is acknowledged only after its outcome has been reported, so a crash makes the job run again. Make handlers idempotent; `ctx.occurrence_id` helps with that.
- **Graceful shutdown.** `stop()` does the following in order:
  1. Stops taking new jobs.
  2. Waits up to `shutdown_timeout` for running jobs to finish.
  3. Returns the jobs that are still running to the queue.
  4. Flushes logs and tells the API the instance has left.

## How it works

The SDK implements the language-neutral [worker protocol](https://github.com/Milvasoft/milvaion/blob/master/docs/githubdocs/WORKER-PROTOCOL.md): the RabbitMQ queues and message formats the .NET SDK uses, and on Kafka the [Kafka transport](https://github.com/Milvasoft/milvaion/blob/master/docs/githubdocs/KAFKA-TRANSPORT.md) contract.

## Development

```bash
python -m venv .venv && . .venv/bin/activate
pip install -e ".[test]"
pytest
python examples/basic_worker.py   # configure with MILVAION_* variables
```

The Kafka integration tests run when `MILVAION_KAFKA_BROKERS` points at a broker, for example `MILVAION_KAFKA_BROKERS=localhost:9092 pytest tests/test_kafka_integration.py`; otherwise they are skipped. Each test uses its own topic prefix.
