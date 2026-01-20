---
id: reliability
title: Reliability
sidebar_position: 8
description: Retries, DLQ, failure handling, and system resilience.
---


# Reliability Patterns

This guide covers Milvaion's built-in reliability mechanisms including retry policies, dead letter queues, zombie detection, and idempotency patterns.

## Overview

Milvaion implements multiple reliability patterns:

| Pattern | Purpose | How It Works |
|---------|---------|--------------|
| **Retry with Backoff** | Recover from transient failures | Exponential delay between attempts |
| **Dead Letter Queue** | Isolate permanently failed jobs | RabbitMQ DLX routing |
| **Zombie Detection** | Recover stuck jobs | Background service monitoring |
| **Distributed Locking** | Prevent duplicate dispatch | Redis locks |
| **Idempotency** | Safe re-execution | CorrelationId tracking |
| **Auto-Disable** | Circuit breaker for failing jobs | Consecutive failure threshold |
| **Graceful Shutdown** | No data loss on stop | CancellationToken propagation |

---

## Retry with Exponential Backoff

### How It Works

When a job throws an exception:

```
1. Worker catches exception
2. Worker NACKs the message (no requeue)
3. Worker waits based on exponential backoff
4. Worker republishes to main queue with retry count
5. Worker consumes and retries
6. After max retries → Dead Letter Queue
```

### Configuration

```json
{
  "JobConsumers": {
    "SendEmailJob": {
      "MaxRetries": 5,
      "BaseRetryDelaySeconds": 10
    }
  }
}
```

### Backoff Schedule

### Exponential Backoff Formula

The retry delay is calculated using the following formula:

```
Delay = BaseRetryDelaySeconds × 2^(attempt - 1)
```

**Example** (`BaseRetryDelaySeconds = 10`):

```
Attempt 1: 0s   (immediate)
Attempt 2: 10s
Attempt 3: 20s
Attempt 4: 40s
Attempt 5: 80s
Attempt 6: DLQ ~2.5 min total

```

> The first attempt is executed immediately and does not apply a delay.
> Exponential backoff starts from the second attempt.

> Attempt numbering starts at 1.
> The delay formula is applied only for attempts ≥ 2.

### Retry Headers

Each retry includes metadata in RabbitMQ headers:

```
x-retry-count: 3
x-max-retries: 5
x-original-correlation-id: abc-123
```

---

## Dead Letter Queue (DLQ)

### What Is It?

Jobs that fail after all retry attempts are moved to a **Dead Letter Queue** for manual review. This prevents:

- Infinite retry loops
- Resource waste on permanent failures
- Log pollution

### DLQ Flow

```
Job fails after MaxRetries
        ↓
Worker publishes to DLQ exchange (milvaion.dlx)
        ↓
FailedOccurrenceHandler consumes from DLQ queue
        ↓
Creates FailedOccurrence record in PostgreSQL
        ↓
Occurrence status updated to Failed
        ↓
Visible in Dashboard → Failed Executions
```

### Viewing Failed Jobs

**Dashboard**: Navigate to **Failed Executions**

**API**:
```bash
curl http://localhost:5000/api/v1/failed-occurrences
```

### Resolving Failed Jobs

Mark as resolved with notes:

```bash
curl -X PUT http://localhost:5000/api/v1/failed-occurrences/{id} \
  -H "Content-Type: application/json" \
  -d '{
    "resolutionNote": { "value": "Fixed data and requeued manually", "isUpdated": true },
    "resolutionAction": { "value": "Manually resolved", "isUpdated": true }
  }'
```

### Manual Retry

Re-trigger a failed job:

```bash
curl -X POST http://localhost:5000/api/v1/jobs/job/trigger \
  -H "Content-Type: application/json" \
  -d '{"jobId": "YOUR_JOB_ID", "reason": "Manual retry after fix"}'
```

---

## Zombie Detection

### What Is a Zombie Job?

A **zombie job** is an occurrence stuck in `Running` or `Queued` status because:

- Worker crashed mid-execution
- Network partition during processing
- Worker ran out of memory
- Process was killed unexpectedly
- Scheduler or worker can't access RabbitMQ

### How Detection Works

The `ZombieOccurrenceDetector` background service:

1. Runs every 5 minutes (configurable)
2. Queries for occurrences stuck beyond threshold
3. Marks them as `Failed` with reason "Zombie detected"
4. Optionally requeues for retry

### Configuration

```json
{
  "MilvaionConfig": {
    "ZombieDetector": {
      "Enabled": true,
      "IntervalMinutes": 5,
      "ThresholdMinutes": 10
    }
  }
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `true` | Enable zombie detection |
| `CheckIntervalSeconds` | `300` | Interval (in seconds) between zombie detection checks. |
| `ZombieTimeoutMinutes` | `10` | Time before considered zombie |

### Per-Job Timeout

Jobs can override the global threshold:

```json
{
  "JobConsumers": {
    "LongRunningJob": {
      "ExecutionTimeoutSeconds": 7200
    }
  }
}
```

This job won't be marked as zombie for 2 hours.

---

## Distributed Locking

### Why Locking?

When running multiple API instances, you need to prevent:

- Same job dispatched twice
- Race conditions during cron trigger
- Duplicate occurrences created

### Redis Lock Implementation

```
1. Dispatcher polls Redis for due jobs
2. For each job, attempt to acquire lock:
   SET Milvaion:JobScheduler:lock:{jobId} {instanceId} NX EX 600
3. If lock acquired → dispatch job
4. If lock exists → skip (another instance handling)
5. Release lock after dispatch
```

### Lock TTL

Default: **10 minutes** (600 seconds)

This ensures that even if the dispatcher crashes, the lock will expire and another instance can take over.

---

## Idempotency

### The Problem

Due to at-least-once delivery, a job might execute multiple times if:

- Worker crashes after completing but before ACK
- Network partition during ACK
- Message redelivered after timeout

### The Solution: CorrelationId

Every occurrence has a unique `CorrelationId`. Workers track completed jobs:

```csharp
// Worker checks local store before executing
if (await _localStore.IsJobFinalizedAsync(correlationId))
{
    // Already completed - ACK and skip
    await SafeAckAsync(deliveryTag);
    return;
}

// Execute job
await ExecuteJobAsync(job);

// Mark as finalized
await _localStore.FinalizeJobAsync(correlationId, status);
```

### Making Your Jobs Idempotent

Design jobs to be safe for re-execution:

```csharp
public async Task ExecuteAsync(IJobContext context)
{
    var data = JsonSerializer.Deserialize<OrderJobData>(context.Job.JobData);
    
    // Check if already processed using business key
    var existingOrder = await _db.Orders.FirstOrDefaultAsync(o => o.ExternalId == data.OrderId);
    
    if (existingOrder != null)
    {
        context.LogInformation($"Order {data.OrderId} already processed, skipping");
        return;
    }
    
    // Process order
    await ProcessOrderAsync(data);
}
```

---

## Auto-Disable (Circuit Breaker)

### What Is It?

If a job fails repeatedly (e.g., due to misconfiguration), auto-disable prevents:

- Wasted compute resources
- Log spam
- Alert fatigue

### How It Works

```
Job fails → Increment consecutiveFailureCount
        ↓
Check: consecutiveFailureCount >= threshold?
        ↓
Yes → Set job.isActive = false
        ↓
Log warning, send notification
```

### Configuration

**Global (API):**
```json
{
  "MilvaionConfig": {
    "JobAutoDisable": {
      "Enabled": true,
      "ConsecutiveFailureThreshold": 5,
      "FailureWindowMinutes": 60,
      "AutoReEnableAfterCooldown": false,
      "AutoReEnableCooldownMinutes": 30
    }
  }
}
```

**Per-Job (via Dashboard or API):**
```json
{
  "autoDisableSettings": {
    "enabled": true,
    "threshold": 10
  }
}
```

### Re-enabling a Job

1. **Dashboard**: Go to job detail → Click "Re-enable"
2. **API**: Update `isActive` to `true`

```bash
curl -X PUT http://localhost:5000/api/v1/jobs/job \
  -H "Content-Type: application/json" \
  -d '{
    "id": "YOUR_JOB_ID",
    "isActive": { "value": true, "isUpdated": true }
  }'
```

---

## Graceful Shutdown

### Worker Shutdown Flow

When a worker receives SIGTERM:

```
1. Stop accepting new messages (cancel consumer)
2. Set CancellationToken for running jobs
3. Wait for running jobs to complete (with timeout)
4. ACK all completed jobs
5. Exit cleanly
```

### Implementation

```csharp
// In your job - check cancellation
public async Task ExecuteAsync(IJobContext context)
{
    foreach (var item in items)
    {
        // Check before each unit of work
        context.CancellationToken.ThrowIfCancellationRequested();
        
        await ProcessItemAsync(item, context.CancellationToken);
    }
}
```

### Shutdown Timeout

Workers wait up to 30 seconds for jobs to complete. Configure in Kubernetes:

```yaml
spec:
  terminationGracePeriodSeconds: 60
```

---

## Offline Resilience

### What Is It?

Workers can continue processing when network connectivity is lost:

1. **Status updates** queued in local SQLite
2. **Logs** stored locally
3. **Periodic sync** attempts when online
4. **Automatic replay** on reconnection

### Configuration

```json
{
  "Worker": {  
    "OfflineResilience": {
      "Enabled": true,
      "LocalStoragePath": "./worker_data",
      "SyncIntervalSeconds": 30,
      "MaxSyncRetries": 3,
      "CleanupIntervalHours": 1,
      "RecordRetentionDays": 1
    }
  }
}
```

### Flow

```
RabbitMQ unavailable?
        ↓
Yes → Store status/logs in SQLite
        ↓
Background sync service checks every 30s
        ↓
RabbitMQ available ? Replay queued messages
        ↓
Clear synced records after 7 days
```

---

## Best Practices

### 1. Set Appropriate Timeouts

```json
{
  "JobConsumers": {
    "QuickApiCall": { "ExecutionTimeoutSeconds": 30 },
    "EmailCampaign": { "ExecutionTimeoutSeconds": 300 },
    "DataMigration": { "ExecutionTimeoutSeconds": 86400 }
  }
}
```

### 2. Distinguish Transient vs Permanent Errors

```csharp
try
{
    await _api.CallAsync();
}
catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable)
{
    // Transient - will retry
    throw;
}
catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
{
    // Permanent - log and fail immediately
    context.LogError("Invalid request, will not retry");
    throw;
}
```

### 3. Use Idempotency Keys

```csharp
// Use business key for deduplication
var idempotencyKey = $"order-{data.OrderId}";
if (await _cache.ExistsAsync(idempotencyKey))
{
    context.LogInformation("Already processed");
    return;
}

await ProcessAsync(data);
await _cache.SetAsync(idempotencyKey, "1", TimeSpan.FromDays(7));
```

### 4. Monitor DLQ Size

Alert when DLQ grows:

```sql
SELECT COUNT(*) FROM "FailedOccurrences" 
WHERE "Resolved" = false 
AND "FailedAt" > NOW() - INTERVAL '24 hours';
```

---

## What's Next?

- **[Scaling](09-scaling.md)** - Horizontal scaling strategies
- **[Monitoring](10-monitoring.md)** - Metrics and alerting
- **[Database Maintenance](11-maintenance.md)** - Cleanup and retention
