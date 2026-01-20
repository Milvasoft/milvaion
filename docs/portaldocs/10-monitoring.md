---
id: monitoring
title: Monitoring
sidebar_position: 10
description: Metrics, logging, dashboards, and alerting for Milvaion.
---


# Monitoring & Observability

This guide covers monitoring Milvaion in production, including health checks, metrics, logging, and alerting.

## Health Checks

### API Health Endpoints

| Endpoint | Purpose | Use For |
|----------|---------|---------|
| `/health/live` | Is the process running? | Kubernetes liveness probe |
| `/health/ready` | Are dependencies healthy? | Kubernetes readiness probe |
| `/health` | Full health with details | Debugging, dashboards |

### Liveness Check

```bash
curl http://localhost:5000/api/v1/healthcheck/live
```

Response:
```json
{
  "status": "Healthy",
  "timestamp": "2026-01-14T17:55:12.5466734Z",
  "uptime": "16.05:34:46.9359664"
}
```

### Readiness Check

```bash
curl http://localhost:5000/api/v1/healthcheck/ready
```

Response:
```json
{
  "status": "Healthy",
  "duration": "00:00:00.0015505",
  "timestamp": "2026-01-14T17:55:39.7914455Z",
  "checks": [
    {
      "name": "PostgreSQL",
      "status": "Healthy",
      "description": "PostgreSQL database connection is healthy",
      "duration": "00:00:00.0014398",
      "tags": [
        "database",
        "sql"
      ],
      "data": {
        "DatabaseName": "MilvaionDb",
        "ConnectionStatus": "Connected",
        "ProviderName": "Npgsql.EntityFrameworkCore.PostgreSQL"
      }
    },
    {
      "name": "Redis",
      "status": "Healthy",
      "description": "Redis connection is healthy",
      "duration": "00:00:00.0004801",
      "tags": [
        "redis",
        "cache"
      ],
      "data": {
        "ConnectionStatus": "Connected",
        "Database": "0"
      }
    },
    {
      "name": "RabbitMQ",
      "status": "Healthy",
      "description": "RabbitMQ connection is healthy",
      "duration": "00:00:00.0000037",
      "tags": [
        "rabbitmq",
        "messaging"
      ],
      "data": {
        "ConnectionStatus": "Connected",
        "Host": "rabbitmq",
        "Port": "5672",
        "IsOpen": "True"
      }
    }
  ]
}
```

### Kubernetes Probes

```yaml
spec:
  containers:
    - name: api
      livenessProbe:
        httpGet:
          path: /api/v1/healthcheck/live
          port: 8080
        initialDelaySeconds: 30
        periodSeconds: 10
        failureThreshold: 3
      readinessProbe:
        httpGet:
          path: /api/v1/healthcheck/ready
          port: 8080
        initialDelaySeconds: 10
        periodSeconds: 5
        failureThreshold: 3
```

### Worker Health

Workers support two health check approaches: **file-based** (for Console Workers) and **HTTP endpoint-based** (for API Workers).

#### Configuration

Enable health checks in `appsettings.json`:

```json
{
  "Worker": {
    "HealthCheck": {
      "Enabled": true,
      "LiveFilePath": "/tmp/live",
      "ReadyFilePath": "/tmp/ready",
      "IntervalSeconds": 30
    }
  }
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `false` | Enable/disable health checks |
| `LiveFilePath` | `/tmp/live` | File path for liveness probe |
| `ReadyFilePath` | `/tmp/ready` | File path for readiness probe |
| `IntervalSeconds` | `30` | Health check interval |

#### Option 1: Console Worker (File-Based)

For workers without HTTP endpoints, use `AddFileHealthCheck()`:

```csharp
using Microsoft.Extensions.Hosting;
using Milvasoft.Milvaion.Sdk.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Register Worker SDK
builder.Services.AddMilvaionWorkerWithJobs(builder.Configuration);

// Add file-based health checks (Redis + RabbitMQ)
builder.Services.AddFileHealthCheck(builder.Configuration);

var host = builder.Build();
await host.RunAsync();
```

**Kubernetes probes:**

```yaml
spec:
  containers:
    - name: worker
      livenessProbe:
        exec:
          command: ["test", "-f", "/tmp/live"]
        initialDelaySeconds: 30
        periodSeconds: 30
        failureThreshold: 3
      readinessProbe:
        exec:
          command: ["test", "-f", "/tmp/ready"]
        initialDelaySeconds: 10
        periodSeconds: 10
        failureThreshold: 3
```

**Docker Compose healthcheck:**

```yaml
worker:
  healthcheck:
    test: ["CMD", "test", "-f", "/tmp/live"]
    interval: 30s
    timeout: 5s
    retries: 3
    start_period: 30s
```

#### Option 2: API Worker (HTTP Endpoints)

For workers with HTTP endpoints, use `AddHealthCheckEndpoints()` and `UseHealthCheckEndpoints()`:

```csharp
using Milvasoft.Milvaion.Sdk.Worker;
using Milvasoft.Milvaion.Sdk.Worker.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// Add health checks (Redis + RabbitMQ)
builder.Services.AddHealthChecks()
                .AddCheck<RedisHealthCheck>("Redis", tags: ["redis", "cache"])
                .AddCheck<RabbitMQHealthCheck>("RabbitMQ", tags: ["rabbitmq", "messaging"]);

// Register Worker SDK
builder.Services.AddMilvaionWorkerWithJobs(builder.Configuration);

// Register health check endpoint services
builder.Services.AddHealthCheckEndpoints(builder.Configuration);

var app = builder.Build();

// Map health check endpoints
app.UseHealthCheckEndpoints(builder.Configuration);

await app.RunAsync();
```

**Available endpoints:**

| Endpoint | Purpose | Response |
|----------|---------|----------|
| `/health` | Simple check | `"Ok"` |
| `/health/live` | Liveness probe | `{ status, timestamp, uptime }` |
| `/health/ready` | Readiness probe | `{ status, duration, checks[] }` |
| `/health/startup` | Startup probe | `{ status, timestamp, uptime }` |

**Kubernetes probes:**

```yaml
spec:
  containers:
    - name: api-worker
      livenessProbe:
        httpGet:
          path: /health/live
          port: 8080
        initialDelaySeconds: 30
        periodSeconds: 10
        failureThreshold: 3
      readinessProbe:
        httpGet:
          path: /health/ready
          port: 8080
        initialDelaySeconds: 10
        periodSeconds: 5
        failureThreshold: 3
      startupProbe:
        httpGet:
          path: /health/startup
          port: 8080
        initialDelaySeconds: 5
        periodSeconds: 5
        failureThreshold: 30
```

#### Health Check Behavior

The SDK includes built-in health checks for:

| Check | What it Verifies |
|-------|------------------|
| **Redis** | Connection status via `PING` command |
| **RabbitMQ** | Connection status via `IConnectionMonitor` |

**File-based health check logic:**

- **Live file exists**: Worker process is running and not completely unhealthy
- **Ready file exists**: All health checks (Redis, RabbitMQ) are healthy
- Files are deleted on graceful shutdown
- Files are updated every `IntervalSeconds`

---

## Dashboard Metrics

### Built-in Statistics

Access via Dashboard home or API:

```bash
curl http://localhost:5000/api/v1/dashboard
```

```json
{
  "isSuccess": true,
  "statusCode": 200,
  "messages": [
    {
      "key": "",
      "message": "Operation successful!",
      "type": 1
    }
  ],
  "data": {
    "totalExecutions": 3546,
    "queuedJobs": 0,
    "completedJobs": 3513,
    "failedOccurrences": 32,
    "cancelledJobs": 1,
    "timedOutJobs": 0,
    "runningJobs": 0,
    "averageDuration": 2509.362653003131,
    "successRate": 99.06937394247038,
    "totalWorkers": 1,
    "totalWorkerInstances": 1,
    "workerCurrentJobs": 0,
    "workerMaxCapacity": 128,
    "workerUtilization": 0,
    "executionsPerMinute": 10,
    "executionsPerSecond": 0, // It means lower than 1
    "peakExecutionsPerMinute": 10
  },
  "metadatas": []
}
```

### Worker Status

```bash
curl http://localhost:5000/api/v1/workers
```

```json
[
  {
  "isSuccess": true,
  "statusCode": 200,
  "messages": [],
  "data": [
    {
      "workerId": "test-worker",
      "displayName": "test-worker (test-worker-172a5243)",
      "routingPatterns": {
        "AlwaysFailingJob": "alwaysfailing.*",
        "LongRunningTestJob": "longrunningtest.*",
        "NonParallelJob": "nonparallel.*",
        "SendEmailJob": "sendemail.*",
        "TestJob": "test.*"
      },
      "jobNames": [
        "AlwaysFailingJob",
        "LongRunningTestJob",
        "NonParallelJob",
        "SendEmailJob",
        "TestJob"
      ],
      "currentJobs": 0,
      "status": "Active",
      "lastHeartbeat": "2026-01-14T18:04:52.0539333+00:00",
      "registeredAt": "2026-01-14T18:01:27.1426831+00:00",
      "version": "1.0.0.0",
      "metadata": "{\"ProcessorCount\":16,\"OSVersion\":\"Unix 6.6.87.1\",\"RuntimeVersion\":\"10.0.1\",\"JobConfigs\":[{\"JobType\":\"AlwaysFailingJob\",\"ConsumerId\":\"alwaysfailing-consumer\",\"MaxParallelJobs\":8,\"ExecutionTimeoutSeconds\":30},{\"JobType\":\"LongRunningTestJob\",\"ConsumerId\":\"longrunning-consumer\",\"MaxParallelJobs\":8,\"ExecutionTimeoutSeconds\":10},{\"JobType\":\"NonParallelJob\",\"ConsumerId\":\"nonparallel-consumer\",\"MaxParallelJobs\":1,\"ExecutionTimeoutSeconds\":30},{\"JobType\":\"SendEmailJob\",\"ConsumerId\":\"email-consumer\",\"MaxParallelJobs\":16,\"ExecutionTimeoutSeconds\":600},{\"JobType\":\"TestJob\",\"ConsumerId\":\"test-consumer\",\"MaxParallelJobs\":32,\"ExecutionTimeoutSeconds\":120}]}",
      "instances": [
        {
          "instanceId": "test-worker-172a5243",
          "hostName": "1fc7768572fd",
          "ipAddress": "172.18.0.6",
          "currentJobs": 0,
          "status": 0,
          "lastHeartbeat": "2026-01-14T18:04:52.0539333+00:00",
          "registeredAt": "2026-01-14T18:01:27.1454977+00:00"
        }
      ]
    }
  ],
  "metadatas": []
}
]
```

---

## Logging

### Structured Logging Setup

Configure Serilog for production:

```json
{
  "Serilog": {
    "Using": ["Serilog.Sinks.Console", "Serilog.Sinks.Seq"],
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "System": "Warning"
      }
    },
    "WriteTo": [
      {
        "Name": "Console",
        "Args": {
          "formatter": "Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact"
        }
      },
      {
        "Name": "Seq",
        "Args": {
          "serverUrl": "http://seq:5341"
        }
      }
    ],
    "Enrich": ["FromLogContext", "WithMachineName", "WithThreadId"],
    "Properties": {
      "Application": "Milvaion.Api"
    }
  }
}
```

### Log Correlation

Use `CorrelationId` to trace a job across services:

```bash
# Find all logs for a specific job execution
grep "corr-789" /var/log/milvaion/*.log
```

Or in Seq/ELK:
```
CorrelationId = "corr-789"
```

---

## OpenTelemetry Integration

Milvaion exposes metrics via a Prometheus-compatible HTTP endpoint for easy scraping by monitoring tools.

### Metrics Endpoint

Access metrics at:

```bash
curl http://localhost:5000/metrics
```

**Example output:**

```
# HELP http_server_request_duration_seconds Duration of HTTP server requests.
# TYPE http_server_request_duration_seconds histogram
http_server_request_duration_seconds_bucket{http_request_method="GET",url_scheme="http",le="0.005"} 1234
...

# HELP process_cpu_time_seconds CPU time consumed by the process.
# TYPE process_cpu_time_seconds gauge
process_cpu_time_seconds 45.23

# HELP http_server_active_requests Number of active HTTP server requests.
# TYPE http_server_active_requests updowncounter
http_server_active_requests 5
```

### Configuration

Configure OpenTelemetry in `appsettings.json`:

```json
{
  "MilvaionConfig": {
    "OpenTelemetry": {
      "Service": "milvaion-backend",
      "Environment": "production",
      "Job": "app-metrics",
      "Instance": "milvaion-prod-01"
    }
  }
}
```

| Setting | Description |
|---------|-------------|
| `Service` | Service name for metrics labels |
| `Environment` | Environment name (dev, test, prod) |
| `Job` | Prometheus job label |
| `Instance` | Instance identifier |

### Available Metrics

| Metric | Type | Description |
|--------|------|-------------|
| `http_server_request_duration_seconds` | Histogram | HTTP request duration |
| `http_server_active_requests` | Gauge | Active HTTP connections |
| `http_client_request_duration_seconds` | Histogram | Outgoing HTTP request duration |
| `process_cpu_time_seconds` | Gauge | CPU time consumed |
| `process_memory_bytes` | Gauge | Memory usage |
| `db_client_operation_duration_seconds` | Histogram | Database query duration |

### Prometheus Scrape Config

```yaml
# prometheus.yml
scrape_configs:
  - job_name: 'milvaion-api'
    static_configs:
      - targets: ['milvaion-api:5000']
    metrics_path: '/metrics'
    scrape_interval: 15s
```

### Tracing

Tracing is configured with the following instrumentations:

- **ASP.NET Core**: HTTP request spans
- **HttpClient**: Outgoing HTTP calls
- **Npgsql**: PostgreSQL queries
- **Entity Framework Core**: ORM operations

---

## Alerting

### Critical Alerts

| Condition | Severity | Action |
|-----------|----------|--------|
| API health check failing | Critical | Page on-call |
| All workers offline | Critical | Page on-call |
| DLQ depth > 100 | High | Investigate failures |
| Success rate < 95% | High | Check failing jobs |
| Queue depth growing | Medium | Scale workers |
| Zombie jobs detected | Medium | Check worker health |

### Prometheus Alert Rules

```yaml
groups:
  - name: milvaion
    rules:
      - alert: MilvaionApiDown
        expr: up{job="milvaion-api"} == 0
        for: 1m
        labels:
          severity: critical
        annotations:
          summary: "Milvaion API is down"

      - alert: MilvaionNoActiveWorkers
        expr: count(milvaion_worker_active) == 0
        for: 2m
        labels:
          severity: critical
        annotations:
          summary: "No active Milvaion workers"

      - alert: MilvaionHighFailureRate
        expr: |
          rate(milvaion_jobs_failed[5m]) / 
          rate(milvaion_jobs_completed[5m]) > 0.1
        for: 5m
        labels:
          severity: high
        annotations:
          summary: "Job failure rate > 10%"

      - alert: MilvaionDLQGrowing
        expr: milvaion_dlq_depth > 50
        for: 10m
        labels:
          severity: high
        annotations:
          summary: "Dead letter queue has {{ $value }} messages"
```

### Grafana Dashboard

Key panels to include:

1. **Jobs Overview**
   - Jobs dispatched/minute
   - Success vs failure rate
   - Average duration

2. **Worker Health**
   - Active workers count
   - Jobs per worker
   - Heartbeat status

3. **Queue Status**
   - RabbitMQ queue depth
   - Messages published/consumed
   - DLQ depth

4. **Infrastructure**
   - Redis memory/connections
   - PostgreSQL connections
   - API response times

---

## RabbitMQ Monitoring

### Management UI

Access at `http://localhost:15672`:

- **Overview**: Message rates, connections
- **Queues**: Depth, consumers, message rates
- **Exchanges**: Routing statistics

### Key Metrics

| Metric | Healthy Range | Action if Exceeded |
|--------|---------------|-------------------|
| Queue depth | < 1000 | Scale workers |
| Unacked messages | < 100 | Check worker health |
| Memory usage | < 80% | Add RAM or scale |
| Disk alarm | Not triggered | Add disk space |

### CLI Monitoring

```bash
# Queue status
docker exec milvaion-rabbitmq rabbitmqctl list_queues name messages consumers

# Connection count
docker exec milvaion-rabbitmq rabbitmqctl list_connections
```

---

## Redis Monitoring

### Key Metrics

| Metric | Command | Healthy Range |
|--------|---------|---------------|
| Memory used | `INFO memory` | < 80% maxmemory |
| Connected clients | `INFO clients` | < 10000 |
| Commands/sec | `INFO stats` | Varies |
| Keyspace | `INFO keyspace` | Growing slowly |

### CLI Commands

```bash
# Memory info
docker exec milvaion-redis redis-cli INFO memory

# Slow queries
docker exec milvaion-redis redis-cli SLOWLOG GET 10

# Active keys
docker exec milvaion-redis redis-cli DBSIZE
```

---

## Database Monitoring

### PostgreSQL Key Metrics

| Metric | Query | Threshold |
|--------|-------|-----------|
| Active connections | `SELECT count(*) FROM pg_stat_activity` | < max_connections |
| Long-running queries | `SELECT * FROM pg_stat_activity WHERE state = 'active' AND query_start < now() - interval '5 minutes'` | 0 |
| Table bloat | `SELECT pg_size_pretty(pg_total_relation_size('JobOccurrences'))` | Monitor growth |

### Useful Queries

```sql
-- Occurrence count by status (last 24h)
SELECT "Status", COUNT(*) 
FROM "JobOccurrences" 
WHERE "CreatedAt" > NOW() - INTERVAL '24 hours'
GROUP BY "Status";

-- Slowest jobs (avg duration)
SELECT j."JobType", AVG(o."DurationMs") as avg_ms, COUNT(*) as count
FROM "JobOccurrences" o
JOIN "ScheduledJobs" j ON o."JobId" = j."Id"
WHERE o."Status" = 2 -- Completed
GROUP BY j."JobType"
ORDER BY avg_ms DESC
LIMIT 10;

-- Failed jobs by type (last 7 days)
SELECT j."JobType", COUNT(*) as failures
FROM "JobOccurrences" o
JOIN "ScheduledJobs" j ON o."JobId" = j."Id"
WHERE o."Status" = 3 -- Failed
AND o."CreatedAt" > NOW() - INTERVAL '7 days'
GROUP BY j."JobType"
ORDER BY failures DESC;
```

---

## Troubleshooting

### Jobs Not Executing

1. Check dispatcher is running: `docker logs milvaion-api | grep -i dispatch`
2. Check workers are registered: `curl http://localhost:5000/api/v1/workers`
3. Check RabbitMQ queues: http://localhost:15672
4. Check Redis scheduled jobs:
   ```bash
   docker exec milvaion-redis redis-cli ZRANGE "Milvaion:JobScheduler:scheduled_jobs" 0 -1 WITHSCORES
   ```

### High Memory Usage

1. Check for large job payloads
2. Check occurrence log sizes
3. Enable database cleanup jobs
4. Check for memory leaks in custom jobs

### Slow Dashboard

1. Check PostgreSQL query performance
2. Add indexes if missing
3. Increase API connection pool
4. Enable response caching

---

## What's Next?

- **[Database Maintenance](11-maintenance.md)** - Cleanup and retention policies
