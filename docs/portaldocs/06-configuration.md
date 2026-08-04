---
id: configuration
title: Configuration
sidebar_position: 6
description: Complete configuration reference for Milvaion API, workers, and infrastructure.
---

# Configuration Reference

This page documents all configuration options for Milvaion API and Workers.

## Milvaion API Configuration

### appsettings.json Structure

```json
{
  "ConnectionStrings": {
    "DefaultConnectionString": "Host=your_host;Port=5432;Database=MilvaionDb;Username=your_username;Password=your_password;Pooling=true;Minimum Pool Size=20;Maximum Pool Size=100;Connection Lifetime=900;Connection Idle Lifetime=180;Command Timeout=30;Include Error Detail=true"
  },
  "MilvaionConfig": {
    "Logging": {
      "Seq": {
        "Enabled": true,
        "Uri": "http://seq:5341"
      }
    },
    "OpenTelemetry": {
      "Service": "milvaion-backend",
      "Environment": "milvaion-test",
      "Job": "app-metrics",
      "Instance": "milvaion-test",
      "CollectorUrl": "http://grafana-alloy:4317"
    },
    "Redis": {
      "ConnectionString": "redis:6379",
      "Password": "",
      "Database": 0,
      "ConnectTimeout": 5000,
      "SyncTimeout": 5000,
      "KeyPrefix": "Milvaion:JobScheduler:",
      "DefaultLockTtlSeconds": 600
    },
    "RabbitMQ": {
      "Host": "rabbitmq",
      "Port": 5672,
      "Username": "guest",
      "Password": "guest",
      "VirtualHost": "/",
      "Durable": true,
      "AutoDelete": false,
      "ConnectionTimeout": 30,
      "Heartbeat": 60,
      "AutomaticRecoveryEnabled": true,
      "NetworkRecoveryInterval": 10,
      "QueueDepthWarningThreshold": 100,
      "QueueDepthCriticalThreshold": 500,
      "ManagementEnabled": false,
      "ManagementPort": 15672
    },
    "JobDispatcher": {
      "Enabled": true,
      "PollingIntervalSeconds": 1,
      "BatchSize": 100,
      "LockTtlSeconds": 600,
      "EnableStartupRecovery": true,
      "ZombieThresholdMinutes": 2,
      "MaxRetryAttempts": 3,
      "RetryDelayMilliseconds": 1000
    },
    "WorkerHealthMonitor": {
      "Enabled": true,
      "CheckIntervalSeconds": 30,
      "HeartbeatTimeoutSeconds": 120,
      "JobHeartbeatTimeoutSeconds": 300
    },
    "WorkerAutoDiscovery": {
      "Enabled": true
    },
    "ZombieOccurrenceDetector": {
      "Enabled": true,
      "CheckIntervalSeconds": 300,
      "ZombieTimeoutMinutes": 10
    },
    "LogCollector": {
      "Enabled": true,
      "BatchSize": 100,
      "BatchIntervalMs": 1000
    },
    "StatusTracker": {
      "Enabled": true,
      "BatchSize": 50,
      "BatchIntervalMs": 100
    },
    "FailedOccurrenceHandler": {
      "Enabled": true
    },
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

### Connection Strings

| Setting | Description |
|---------|-------------|
| `DefaultConnectionString` | PostgreSQL connection string |

### OpenTelemetry

Open telemetry configurations. Set null or empty via environment variables if you don't want export telemetry.

### Redis Configuration

| Setting | Default | Description |
|---------|---------|-------------|
| `ConnectionString` | `localhost:6379` | Redis server address |
| `Password` | `""` | Redis password (if auth enabled) |
| `Database` | `0` | Redis database index (0-15) |
| `ConnectTimeout` | `5000` | Connection timeout in milliseconds |
| `SyncTimeout` | `5000` | Sync timeout for Redis operations in milliseconds. |
| `KeyPrefix` | `Milvaion:JobScheduler:` | Prefix applied to **every** key this application writes: the schedule, job cache, locks, the worker registry and its per-instance counters, and API key entries. Two deployments sharing one Redis instance must use different prefixes, or they will overwrite each other's state. Workers must be given the matching `Worker:Redis:KeyPrefix`. |
| `DefaultLockTtlSeconds` | `600` | Default lock TTL in seconds. |

#### Why the key prefix matters

Redis grants ACLs on **key patterns**, not on database numbers. Selecting a different
`Database` therefore isolates nothing: a user allowed to read `workers:*` reads it in every
database of the instance.

The prefix is what makes isolation expressible. Give each deployment its own, and an ACL rule
scoped to that namespace is enough to keep it separate from every other one sharing the
instance:

```
ACL SETUSER tenant-a on >secret ~Milvaion:TenantA:* +@all
ACL SETUSER tenant-b on >secret ~Milvaion:TenantB:* +@all
```

This only holds if **no key escapes the prefix**. Every key Milvaion writes is built from
`KeyPrefix` — the schedule, the job cache, the locks, the worker registry with its
per-instance counters, and the cached API keys — precisely so that a single pattern covers the
whole footprint. A workload writing outside it would be reachable by every tenant, or by none.

Workers must be configured with the matching `Worker:Redis:KeyPrefix`: the cancellation
channel is derived from it, so a worker on a different prefix subscribes to a channel the API
never publishes to and silently stops honouring cancellations.

### RabbitMQ Configuration

| Setting | Default | Description |
|---------|---------|-------------|
| `Host` | `localhost` | RabbitMQ server hostname |
| `Port` | `5672` | AMQP port |
| `Username` | `guest` | RabbitMQ username |
| `Password` | `guest` | RabbitMQ password |
| `VirtualHost` | `/` | Virtual host name |
| `Durable` | `/` | Whether the queue should be durable (survives broker restart). |
| `AutoDelete` | `/` | Whether the queue should auto-delete when no consumers. |
| `QueueType` | `Classic` | RabbitMQ queue type used when declaring queues. Accepted values: `Classic`, `Quorum`. Must match the value used by any connected worker's `Worker:RabbitMQ:QueueType` setting for the same queues, otherwise RabbitMQ rejects the redeclare with a `PRECONDITION_FAILED` error. Changing this value for an already-existing queue requires deleting the queue in RabbitMQ first, since RabbitMQ does not support converting a queue's type in place. |
| `ConnectionTimeout` | `/` | Connection timeout in seconds. |
| `Heartbeat` | `/` | Heartbeat interval in seconds (0 = disabled). |
| `AutomaticRecoveryEnabled` | `/` | Automatic connection recovery enabled. |
| `NetworkRecoveryInterval` | `/` | Network recovery interval in seconds. |
| `QueueDepthWarningThreshold` | `5000` | Message count that triggers a Warning health status. |
| `QueueDepthCriticalThreshold` | `10000` | Message count that triggers a Critical health status. |
| `ManagementEnabled` | `false` | Enable RabbitMQ Management HTTP API integration for richer queue stats and dynamic queue discovery. |
| `ManagementPort` | `15672` | RabbitMQ Management plugin HTTP port. Used when `ManagementEnabled` is `true`. |

### Job Dispatcher Configuration

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `true` | Enable/disable job dispatching |
| `PollingIntervalSeconds` | `1` | How often to check for due jobs |
| `BatchSize` | `100` | Max jobs to dispatch per poll |
| `LockTtlSeconds` | `600` | Distributed lock TTL (10 min) |
| `EnableStartupRecovery` | `true` | Whether to perform zombie job recovery on startup. Default: true. |

### Worker Health Monitor Configuration

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `true` | Enable/disable worker health monitoring |
| `CheckIntervalSeconds` | `30` | Interval between health checks in seconds |
| `HeartbeatTimeoutSeconds` | `120` | Worker heartbeat timeout in seconds. Workers that don't send heartbeat for this duration are marked as zombie. Default: 120 seconds (2 minutes) |
| `JobHeartbeatTimeoutSeconds` | `300` | Job heartbeat timeout in seconds. Running jobs that don't update heartbeat for this duration are marked as Unknown. Default: 300 seconds (5 minutes). |

### Worker Auto Discovery Configuration

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `true` | Enable worker auto discovery |

### Zombie Detector Configuration

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `true` | Enable zombie job detection |
| `CheckIntervalSeconds` | `300` | Interval (in seconds) between zombie detection checks. Default: 300 seconds (5 minutes). |
| `ZombieTimeoutMinutes` | `10` | Timeout (in minutes) before marking a Queued occurrence as zombie. Default: 10 minutes |

### Log Collector Configuration

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `true` | Enable log collection. |
| `BatchSize` | `100` | Batch size for processing log entries. |
| `BatchIntervalMs` | `1000` | Interval in milliseconds between processing batches. |

### Status Tracker Configuration

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `true` | Enable status tracking |
| `BatchSize` | `50` | Batch size for processing status updates. |
| `BatchIntervalMs` | `100` | Interval in milliseconds between processing batches. |


### Failed Occurrence Handler Configuration

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `true` | Enable failed occurrence handling |

### Job Auto Disable Configuration

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `true` | Enable auto-disable jobs on consecutive failures |
| `ConsecutiveFailureThreshold` | `5` | Number of consecutive failures before a job is automatically disabled. Individual jobs can override this with their own AutoDisableThreshold setting. Default: 5 consecutive failures |
| `FailureWindowMinutes` | `60` | Time window in minutes for counting consecutive failures. Failures older than this window don't count towards the threshold. This prevents jobs from being disabled due to old historical failures. Default: 60 minutes (1 hour) |

### BasePath Configuration

Milvaion API supports hosting under a configurable sub-path (e.g. `/milvaion`) so that both the UI and the backend API can be scoped to a URL prefix. This is useful when deploying behind a reverse proxy alongside other services.

| Setting | Default | Description |
|---------|---------|-------------|
| `BasePath` | `""` | URL prefix **this application receives requests on**, applied as the ASP.NET path base. Leave empty to host at the root. Example: `/milvaion` |
| `PublicBasePath` | `""` | URL prefix **the browser addresses the application on**, when it differs from `BasePath`. Empty means "same as `BasePath`", which is correct unless a reverse proxy strips the prefix before forwarding. |

When `BasePath` is set:

- The REST API is available at `{BasePath}/api/v1/...` (e.g. `/milvaion/api/v1/jobs`)
- The SignalR hub is available at `{BasePath}/hubs/jobs`
- The Prometheus metrics endpoint is available at `{BasePath}/metrics`
- The Scalar/OpenAPI documentation is available at `{BasePath}/scalar/v1`
- The SPA (UI) is served at `{BasePath}` and all sub-routes fall back to the SPA index

When `BasePath` is empty or not set, the application is hosted at the root (`/`).

#### No rebuild required

The base path is applied at **startup**, not when the frontend is built. The published image
ships with a placeholder in place of the prefix, and the API substitutes the configured value
into the SPA's assets — the URLs in `index.html`, the loader for lazily routed chunks, and the
PWA manifest scope — as it boots.

One image therefore serves any prefix. Changing the base path means restarting the container,
never rebuilding it.

> `VITE_BASE_PATH` is **not** a runtime setting. It only affects a local `npm run build`, and
> the published image is built with the placeholder regardless. Setting it as a container
> environment variable has no effect; if its value disagrees with the configured base path, the
> API logs a warning at startup saying which value actually applies.

#### Behind a prefix-stripping reverse proxy

Some proxies remove the prefix before forwarding, so the browser asks for `/milvaion/jobs`
while the application receives `/jobs`. The two values then differ:

```yaml
environment:
  - MilvaionConfig__BasePath=              # what this application receives: the root
  - MilvaionConfig__PublicBasePath=/milvaion   # what the browser uses
```

`PublicBasePath` drives everything the browser resolves — asset URLs, the router basename, and
the base URL for API and SignalR calls — while `BasePath` stays the prefix the application is
actually mounted on.

Keeping the prefix end to end is simpler: configure the proxy to pass it through, set only
`BasePath`, and leave `PublicBasePath` empty.

#### Example Configuration

```json
{
  "MilvaionConfig": {
    "BasePath": "/milvaion"
  }
}
```

#### Environment Variable Override

```bash
MilvaionConfig__BasePath=/milvaion
```

#### Docker / docker-compose

```yaml
services:
  milvaion-api:
    image: milvasoft/milvaion-api:latest   # pull from Docker Hub, no rebuild needed
    environment:
      - MilvaionConfig__BasePath=/milvaion
```

To revert to root hosting, remove the override or set it to empty:

```yaml
environment:
  - MilvaionConfig__BasePath=
```

---

## Worker Configuration

### Worker appsettings.json Structure

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "Microsoft": "Debug",
      "System": "Debug"
    }
  },
  "Worker": {
    "WorkerId": "sample-worker-01",
    "MaxParallelJobs": 128,
    "ExecutionTimeoutSeconds": 300,
    "RabbitMQ": {
      "Host": "rabbitmq",
      "Port": 5672,
      "Username": "guest",
      "Password": "guest",
      "VirtualHost": "/"
    },
    "Redis": {
      "ConnectionString": "redis:6379",
      "Password": "",
      "Database": 0,
      "CancellationChannel": "Milvaion:JobScheduler:cancellation_channel"
    },
    "Heartbeat": {
      "Enabled": true,
      "IntervalSeconds": 5
    },
    "OfflineResilience": {
      "Enabled": true,
      "LocalStoragePath": "./worker_data",
      "SyncIntervalSeconds": 30,
      "MaxSyncRetries": 3,
      "CleanupIntervalHours": 1,
      "RecordRetentionDays": 1
    }
  },
  "JobConsumers": {
    "SimpleJob": {
      "ConsumerId": "simple-consumer",
      "MaxParallelJobs": 32,
      "ExecutionTimeoutSeconds": 120,
      "MaxRetries": 3,
      "BaseRetryDelaySeconds": 5,
      "LogUserFriendlyLogsViaLogger": true
    },
    "SendEmailJob": {
      "ConsumerId": "email-consumer",
      "MaxParallelJobs": 16,
      "ExecutionTimeoutSeconds": 600,
      "MaxRetries": 3,
      "BaseRetryDelaySeconds": 5,
      "LogUserFriendlyLogsViaLogger": true
    }
  }
}
```

### Worker Core Settings

| Setting | Default | Required | Description |
|---------|---------|----------|-------------|
| `WorkerId` | - | Yes | Unique identifier for this worker (app-level, user-defined). Example: "test-worker", "email-worker". Same across all replicas/instances. |
| `MaxParallelJobs` | `10` | No | Maximum parallel jobs this instance can run simultaneously. Default: ProcessorCount * 2 (e.g., 8 cores = 16 parallel jobs). |
| `ExecutionTimeoutSeconds` | `3600` | No | Default maximum execution time allowed for jobs (in seconds). If a job exceeds this timeout, it will be cancelled and marked as TimedOut. Default: 3600 seconds (1 hour). Set to 0 or negative value for no timeout (not recommended). Can be overridden per job consumer in JobConsumerOptions. |

### Worker RabbitMQ Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `Host` | `localhost` | RabbitMQ server |
| `Port` | `5672` | AMQP port |
| `Username` | `guest` | Username |
| `Password` | `guest` | Password |
| `VirtualHost` | `/` | Virtual host |
| `RoutingKeyPattern` | `#` | Queue binding pattern. Don't recommended setting up routing patterns. The scheduler and worker will determine this automatically at runtime. |
| `QueueType` | `Classic` | RabbitMQ queue type used when declaring queues. Accepted values: `Classic`, `Quorum`. Must match the API's `MilvaionConfig:RabbitMQ:QueueType` setting for shared queues, otherwise RabbitMQ rejects the redeclare with a `PRECONDITION_FAILED` error. |


### Worker Redis Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `ConnectionString` | `localhost:6379` | Redis server |
| `Password` | `""` | Redis password |
| `Database` | `0` | Redis database index |
| `KeyPrefix` | `Milvaion:JobScheduler:` | Prefix used to build the cancellation pub/sub channel name (`{KeyPrefix}cancellation_channel`). Must match the API's `MilvaionConfig:Redis:KeyPrefix` setting so cancellation signals reach the worker. |
| `CancellationChannel` | `{KeyPrefix}cancellation_channel` | Redis pub/sub cancellation channel. Optional explicit override; if not set, it's derived from `KeyPrefix`. If you set this explicitly, you must set the matching value in the scheduler config too. |

### Heartbeat Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `true` | Send heartbeats to Redis |
| `IntervalSeconds` | `10` | Heartbeat frequency |

### Offline Resilience Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `true` | Enable local SQLite fallback |
| `LocalStoragePath` | `./worker_data` | SQLite database location |
| `SyncIntervalSeconds` | `30` | Interval (in seconds) for syncing pending items to scheduler. Default: 30 seconds |
| `MaxSyncRetries` | `3` | Maximum number of retry attempts for failed sync operations. After max retries, items are marked as synced to prevent blocking. Default: 3 |
| `CleanupIntervalHours` | `1` | Interval (in hours) for cleaning up old synced records. Default: 1 hours |
| `RecordRetentionDays` | `1` | Retention period (in days) for synced records before cleanup. Default: 1 days |

### Job Consumer Settings

Each job type can have its own configuration:

```json
{
  "JobConsumers": {
    "SendEmailJob": {
      "MaxRetries": 5,
      "BaseRetryDelaySeconds": 10,
      "ExecutionTimeoutSeconds": 120
    },
    "GenerateReportJob": {
      "MaxRetries": 2,
      "BaseRetryDelaySeconds": 30,
      "ExecutionTimeoutSeconds": 3600
    }
  }
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `ConsumerId` | `null` | Consumer identifier (user-defined, e.g., "test-consumer"). Used to identify this specific job consumer. |
| `RoutingPattern` | `null` | Routing patterns this consumer handles. Don't recommended setting up routing patterns. The scheduler and worker will determine this automatically at runtime |
| `MaxParallelJobs` | `10` | Maximum parallel jobs this consumer can run simultaneously. RabbitMQ prefetch count. |
| `ExecutionTimeoutSeconds` | `3600` | Maximum execution time allowed for jobs in this consumer. If a job exceeds this timeout, it will be cancelled and marked as TimedOut. Default: 1 hour (3600 seconds). Set to 0 or negative value for no timeout (not recommended). |
| `MaxRetries` | `3` | Maximum number of retry attempts before moving to DLQ. Default: 3 retries. Set to 0 to disable retries (immediate DLQ on failure). |
| `BaseRetryDelaySeconds` | `5` | Base delay in seconds for exponential backoff retry strategy. Actual delay = BaseRetryDelaySeconds * (2 ^ retryAttempt). Example: BaseRetryDelaySeconds=5 → 5s, 10s, 20s, 40s... Default: 5 seconds. |
| `LogUserFriendlyLogsViaLogger`| `false`  | Determine whether user-friendly logs should be logged via configured IMilvaLogger(ILogger). |


---

## Environment Variables

Both API and Workers support environment variable overrides using double-underscore notation:

### API Environment Variables

```bash
# Connection string
ConnectionStrings__DefaultConnectionString=Host=db.prod.local;...

# Redis
MilvaionConfig__Redis__ConnectionString=redis.prod.local:6379
MilvaionConfig__Redis__Password=secretpassword

# RabbitMQ
MilvaionConfig__RabbitMQ__Host=rabbitmq.prod.local
MilvaionConfig__RabbitMQ__Username=milvaion
MilvaionConfig__RabbitMQ__Password=secretpassword

# Dispatcher
MilvaionConfig__JobDispatcher__PollingIntervalSeconds=2
```

### Worker Environment Variables

```bash
# Core
Worker__WorkerId=worker-prod-01
Worker__MaxParallelJobs=20

# RabbitMQ
Worker__RabbitMQ__Host=rabbitmq.prod.local
Worker__RabbitMQ__Username=worker
Worker__RabbitMQ__Password=secretpassword

# Redis
Worker__Redis__ConnectionString=redis.prod.local:6379

# Job-specific
JobConsumers__SendEmailJob__MaxRetries=5
JobConsumers__SendEmailJob__ExecutionTimeoutSeconds=120
```

### Docker Compose Example

```yaml
services:
  milvaion-api:
    image: milvasoft/milvaion-api:latest
    environment:
      - ConnectionStrings__DefaultConnectionString=Host=postgres;...
      - MilvaionConfig__Redis__ConnectionString=redis:6379
      - MilvaionConfig__RabbitMQ__Host=rabbitmq

  email-worker:
    image: my-company/email-worker:latest
    environment:
      - Worker__WorkerId=email-worker-01
      - Worker__RabbitMQ__Host=rabbitmq
      - Worker__Redis__ConnectionString=redis:6379
      - Worker__MaxParallelJobs=50
```

### Kubernetes ConfigMap/Secret Example

```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: worker-config
data:
  Worker__WorkerId: "email-worker"
  Worker__MaxParallelJobs: "20"
  Worker__RabbitMQ__Host: "rabbitmq.default.svc"

---
apiVersion: v1
kind: Secret
metadata:
  name: worker-secrets
stringData:
  Worker__RabbitMQ__Password: "secretpassword"
  Worker__Redis__ConnectionString: "redis.default.svc:6379,password=secret"
```

---

## Common Configuration Patterns

### High-Throughput Email Worker

```json
{
  "Worker": {
    "WorkerId": "email-worker",
    "MaxParallelJobs": 100
  },
  "JobConsumers": {
    "SendEmailJob": {
      "ConsumerId": "email-consumer",
      "MaxParallelJobs": 50,
      "ExecutionTimeoutSeconds": 30,
      "MaxRetries": 5,
      "BaseRetryDelaySeconds": 10,
      "LogUserFriendlyLogsViaLogger": true
    }
  }
}
```

### CPU-Intensive Report Worker

```json
{
  "Worker": {
    "WorkerId": "report-worker",
    "MaxParallelJobs": 4
  },
  "JobConsumers": {
    "GenerateReportJob": {
      "ConsumerId": "report-consumer",
      "MaxParallelJobs": 2,
      "ExecutionTimeoutSeconds": 7200,
      "MaxRetries": 2,
      "BaseRetryDelaySeconds": 60,
      "LogUserFriendlyLogsViaLogger": true
    }
  }
}
```

### Long-Running Data Migration Worker

```json
{
  "Worker": {
    "WorkerId": "migration-worker",
    "MaxParallelJobs": 1,
    "ExecutionTimeoutSeconds": 86400
  },
  "JobConsumers": {
    "DataMigrationJob": {
      "ConsumerId": "migration-consumer",
      "MaxParallelJobs": 1,
      "ExecutionTimeoutSeconds": 86400,
      "MaxRetries": 1,
      "BaseRetryDelaySeconds": 300,
      "LogUserFriendlyLogsViaLogger": false
    }
  }
}
```

---

## What's Next?

- **[Deployment](07-deployment.md)** - Production deployment guide
- **[Reliability](08-reliability.md)** - Retry, DLQ, and error handling
- **[Scaling](09-scaling.md)** - Horizontal scaling strategies
