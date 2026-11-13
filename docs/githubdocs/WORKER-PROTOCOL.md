# Worker Protocol

This document describes how a worker talks to Milvaion, independent of any programming language. The .NET worker SDK (`Milvasoft.Milvaion.Sdk.Worker`) and the Node.js, Go, Python, Java and PHP worker SDKs under `sdks/` all implement it. A worker written in any other language that follows this document works with an unmodified Milvaion API.

A worker needs two connections:

- **RabbitMQ** for everything that flows between the worker and the API: jobs, status updates, logs, heartbeats and registration.
- **Redis** (optional) only to receive cancellation requests. Without it, jobs cannot be cancelled from the dashboard.

## Conventions

- Every message body is UTF-8 JSON.
- The API reads property names **case-insensitively**. The examples use camelCase. The one exception is the `metadata` string inside the registration message, which must use **PascalCase** property names.
- Enums are sent as **numbers**.
- Timestamps are ISO 8601 strings in UTC, for example `2026-11-13T09:00:00.000Z`.
- Ids (`jobId`, `occurrenceId`) are GUID strings.

### Identifiers

| Name | Meaning | Example |
|---|---|---|
| `workerId` | The worker application. Identical on every replica. | `report-worker` |
| `instanceId` | One running process. Generate a new one at every start: `{workerId}-{8 hex chars}`. | `report-worker-4817786d` |
| job name | The name the job is registered and dispatched under (`JobNameInWorker`). Unique within a worker. | `SendEmailJob` |
| `occurrenceId` | One execution of a job. Also called the correlation id. | GUID |

### Enums

`JobOccurrenceStatus`

| Value | Name |
|---|---|
| 0 | Queued |
| 1 | Running |
| 2 | Completed |
| 3 | Failed |
| 4 | Cancelled |
| 5 | TimedOut |
| 6 | Unknown |
| 7 | Skipped |

## Broker topology

All queues are durable and not exclusive or auto-deleted. Every queue declaration must use the same queue type as the API (`MilvaionConfig:RabbitMQ:QueueType`). For a quorum setup, add the argument `x-queue-type: quorum` to every queue a worker declares. RabbitMQ refuses a redeclaration whose arguments differ from the existing queue.

| Name | Kind | Direction | Purpose |
|---|---|---|---|
| `jobs.topic` | topic exchange, durable | API → worker | Job dispatch |
| `dlx_scheduled_jobs` | direct exchange, durable | worker → API | Dead letter exchange |
| `failed_jobs_queue` | queue, bound to `dlx_scheduled_jobs` with key `failed_jobs` | worker → API | Jobs that exhausted retries or failed permanently |
| `scheduled_jobs_queue.{suffix}` | queue, one per job name | API → worker | Jobs of that type |
| `job_status_updates_queue` | queue | worker → API | Status updates |
| `worker_logs_queue` | queue | worker → API | Occurrence logs |
| `worker_heartbeat_queue` | queue | worker → API | Instance heartbeats |
| `worker_registration_queue` | queue | worker → API | Registration |

Worker → API messages are published to the default exchange (`""`) with the queue name as the routing key. Use publisher confirms so a message the broker did not accept is reported as an error.

### Job queues and routing

Each job name gets its own queue and routing pattern:

1. **Routing pattern.** Use the configured value, or generate one: strip a trailing `Job` (case-insensitive) from the job name, lowercase it, and form `{workerId lowercased}.{name}.*`. For example, `SendEmailJob` in `report-worker` becomes `report-worker.sendemail.*`.
2. **Queue name.** In the pattern, replace `*` with `wildcard` and `#` with `all`, then prefix it with `scheduled_jobs_queue.`. The example above becomes `scheduled_jobs_queue.report-worker.sendemail.wildcard`.
3. **Declare the queue** with these arguments, plus the queue type argument if you use quorum queues:
   - `x-dead-letter-exchange: dlx_scheduled_jobs`
   - `x-dead-letter-routing-key: failed_jobs`
4. **Bind the queue** to `jobs.topic` using the pattern.

The worker sends each pattern to the API at registration. The API stores it on the worker's jobs and publishes them with the routing key `pattern` with `*` replaced by `job`, for example `report-worker.sendemail.job`.

Set the consumer prefetch (QoS) of each job queue to the number of jobs of that type the instance may run in parallel. Consume with manual acknowledgement.

## Registration

Publish to `worker_registration_queue` at startup and again after every reconnect. The API keeps registrations in Redis, so a worker that reconnects without re-registering can disappear from the dashboard.

```json
{
  "workerId": "report-worker",
  "instanceId": "report-worker-4817786d",
  "displayName": "report-worker (report-worker-4817786d)",
  "hostName": "worker-pod-1",
  "ipAddress": "10.0.0.12",
  "routingPatterns": { "SendEmailJob": "report-worker.sendemail.*" },
  "jobDataDefinitions": { "SendEmailJob": "{\"type\":\"object\",\"properties\":{\"to\":{\"type\":\"string\"}}}" },
  "jobResultDefinitions": {},
  "jobTypes": ["SendEmailJob"],
  "maxParallelJobs": 16,
  "version": "1.0.0",
  "metadata": "{\"IsExternal\":false,\"ExternalScheduler\":null,\"ProcessorCount\":8,\"OSVersion\":\"Linux 6.8\",\"RuntimeVersion\":\"Node.js v22.16.0\",\"HeartbeatInterval\":30,\"JobConfigs\":[{\"JobType\":\"SendEmailJob\",\"ConsumerId\":\"sendemail-consumer\",\"MaxParallelJobs\":8,\"ExecutionTimeoutSeconds\":600}]}"
}
```

- `jobDataDefinitions` and `jobResultDefinitions` map a job name to a JSON Schema serialized as a string. The dashboard uses the data schema to build the job data form. Both maps are optional.
- `maxParallelJobs` is the capacity of the whole instance.
- `metadata` is a JSON string, not an object, and its property names are PascalCase.

## Heartbeats

Publish to `worker_heartbeat_queue` every `HeartbeatInterval` seconds (default 30). Also publish one immediately when a job starts or ends, at most one per second, so the API's load balancing sees the current job count.

```json
{
  "workerId": "report-worker",
  "instanceId": "report-worker-4817786d",
  "currentJobs": 3,
  "memoryBytes": 123456789,
  "cpuUsagePercent": 12.5,
  "timestamp": "2026-11-13T09:00:00.000Z",
  "isStopping": false
}
```

- `cpuUsagePercent` is process CPU time over wall-clock time since the previous heartbeat, summed across cores, so one fully busy core is 100.
- On graceful shutdown, send one heartbeat with `currentJobs: 0` and `isStopping: true`. The API then releases the instance's capacity immediately instead of waiting for it to time out.

## Receiving a job

The body of a delivery is the scheduled job:

```json
{
  "id": "0193...",
  "displayName": "Send welcome mail",
  "jobNameInWorker": "SendEmailJob",
  "jobData": "{\"to\":\"user@example.com\"}",
  "executeAt": "2026-11-13T09:00:00Z",
  "cronExpression": "0 9 * * *",
  "workerId": "report-worker",
  "routingPattern": "report-worker.sendemail.*",
  "executionTimeoutSeconds": 600,
  "zombieTimeoutMinutes": null,
  "version": 3
}
```

- Additional properties may be present. Ignore the ones you do not use.
- `jobData` is a JSON string, not an object.

Read the occurrence id from the first of these that holds a GUID. A header value may arrive as bytes or as a string.

1. The `OccurrenceId` header
2. The `CorrelationId` header
3. The message's `correlation_id` property

Read the retry count from the `x-retry-count` header. If it is missing, the retry count is 0.

If the body is not valid JSON, reject the message without requeue (`basic.nack`, `requeue=false`). The queue dead-letters it.

## Executing a job

1. If the delivery is marked as redelivered, the previous attempt may have crashed. Publish a `Running` status and a `Warning` log that says so.
2. Increment the instance's job count and send an immediate heartbeat.
3. Publish a `Running` status with `startTime`.
4. Find the handler for `jobNameInWorker` (case-insensitive). If none exists, handle it as a permanent failure with the exception `No IJob implementation registered for type: {name}`.
5. Run the handler with a timeout. Use the first value that is set:
   1. the job's `executionTimeoutSeconds`
   2. the job type's configured timeout
   3. the worker default (3600 s)

   A value of 0 or less means no timeout.
6. While the job runs, publish a `Running` status every `JobHeartbeatIntervalSeconds` (default 60). This refreshes the occurrence's heartbeat so the zombie detector does not mark it `Unknown`.
7. Flush the occurrence's buffered logs.
8. Report the outcome as described in the next section.
9. Decrement the job count and send an immediate heartbeat.

### Outcomes

| Outcome | Status published | Message handling |
|---|---|---|
| Success | `Completed` (2), with `result`, `startTime`, `endTime` and `durationMs` | ack |
| Timeout | `TimedOut` (5), with `exception` | ack |
| Cancelled by request | `Cancelled` (4) | ack |
| Interrupted by the worker shutting down | none | nack with requeue, so the job runs again |
| Failure, retries left | none, the occurrence stays `Running` | republish for retry, then ack |
| Failure, no retries left | `Failed` (3), with `exception` | publish to the DLQ, then ack |
| Permanent failure | `Failed` (3), with `exception` | publish to the DLQ, then ack |

`result` is a string. A structured result is serialized to JSON. If the handler returns nothing, send `Job {name} completed successfully`.

### Retries

The default `MaxRetries` is 3 and the default `BaseRetryDelaySeconds` is 5. A failure with `retryCount < MaxRetries` is retried as follows:

1. Publish a `Warning` log with the category `Retry`, for example `Job failed (attempt 1/3). Retrying in 5s...`.
2. Wait `BaseRetryDelaySeconds * 2^retryCount` seconds. Keep sending job heartbeats during the wait, and do not ack the original message yet. If the worker dies during the wait, the broker redelivers the job.
3. Republish the job body unchanged to the job's own queue on the default exchange, with:
   - `persistent`
   - `correlation_id` set to the occurrence id
   - headers `CorrelationId` (the occurrence id as bytes), `x-retry-count` (`retryCount + 1`) and `MaxRetries`
4. Ack the original message.

### Dead letter queue

Publish to exchange `dlx_scheduled_jobs` with routing key `failed_jobs`, using these properties:

- `persistent`
- `correlation_id` set to the occurrence id
- headers `CorrelationId` (bytes), `x-retry-count` and `MaxRetries`. For a permanent failure, set `MaxRetries` to `0`.

```json
{
  "id": "0193...",
  "displayName": "Send welcome mail",
  "jobNameInWorker": "SendEmailJob",
  "jobData": "{\"to\":\"user@example.com\"}",
  "executeAt": "2026-11-13T09:00:00Z",
  "status": 3,
  "exception": "Max retries (3) exceeded. Original exception: ..."
}
```

Prefix the exception of a permanent failure with `[PERMANENT FAILURE] `. The API creates a failed occurrence record from this message.

## Status updates

Publish to `job_status_updates_queue` as a persistent message:

```json
{
  "occurrenceId": "0193...",
  "jobId": "0193...",
  "workerId": "report-worker",
  "instanceId": "report-worker-4817786d",
  "status": 2,
  "startTime": "2026-11-13T09:00:00.000Z",
  "endTime": "2026-11-13T09:00:04.200Z",
  "durationMs": 4200,
  "result": "{\"sent\":true}",
  "exception": null,
  "messageTimestamp": "2026-11-13T09:00:04.201Z"
}
```

Flush the occurrence's logs before publishing a final status.

## Logs

Publish occurrence logs to `worker_logs_queue` as persistent messages. Send them in batches, for example every 500 ms or every 50 entries.

```json
{
  "logs": [
    {
      "occurrenceId": "0193...",
      "workerId": "report-worker-4817786d",
      "log": {
        "timestamp": "2026-11-13T09:00:01.000Z",
        "level": "Information",
        "message": "Mail sent",
        "data": { "to": "user@example.com" },
        "category": "UserCode",
        "exceptionType": null
      },
      "messageTimestamp": "2026-11-13T09:00:01.000Z"
    }
  ],
  "batchTimestamp": "2026-11-13T09:00:01.500Z",
  "count": 1
}
```

- `level` is one of `Debug`, `Information`, `Warning` or `Error`.
- `workerId` here is the instance id.
- The API also accepts a single log entry, that is one `logs[]` element, as the whole body.

## Cancellation

Subscribe to the Redis pub/sub channel `{KeyPrefix}cancellation_channel`. The default prefix is `Milvaion:JobScheduler:`, and it must match `MilvaionConfig:Redis:KeyPrefix` on the API. Messages look like this:

```json
{ "CorrelationId": "0193...", "JobId": "0193...", "OccurrenceId": "0193...", "Reason": "Cancelled by user" }
```

If `CorrelationId` matches a job running on this instance, stop that job and report it as `Cancelled`. Messages for jobs that are not running here can be ignored, because every instance receives every message.

## Delivery guarantees

Delivery is at least once. A job whose ack is lost, for example because the connection dropped or the worker crashed, is delivered again. Make handlers idempotent, or check the occurrence id before repeating side effects.
