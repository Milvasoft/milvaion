# Milvaion Architecture

This document provides a deep-dive into Milvaion's technical architecture, design decisions, and component interactions.

## Table of Contents

- [Overview](#overview)
- [System Architecture](#system-architecture)
- [Component Details](#component-details)
- [Data Flow](#data-flow)
- [Database Schema](#database-schema)
- [Message Queue Design](#message-queue-design)
- [Caching Strategy](#caching-strategy)
- [Security Architecture](#security-architecture)
- [Scalability Patterns](#scalability-patterns)

---

## Overview

Milvaion is built on the principle of **separation of concerns**, splitting job scheduling from job execution. This distributed architecture enables:

- **Independent scaling** of scheduler and workers
- **Fault isolation** - worker failures don't affect scheduling
- **Heterogeneous workers** - different hardware for different job types
- **Language agnostic workers** - any language that can consume RabbitMQ

### High-Level Architecture

![Milvaion High-Level Architecture](https://portal.milvasoft.com/assets/images/architecture-bb9bd8cc65898fd524913e866f9db9b6.png)

---

## System Architecture

### Onion Architecture

Milvaion follows **Onion Architecture** (also known as Clean Architecture) for the API layer.

### Dependency Rules

1. **Domain** has no dependencies on other layers
2. **Application** depends only on Domain
3. **Infrastructure** depends on Application and Domain
4. **Presentation** depends on all layers

### Project References

```
Milvaion.Api
    └── Milvaion.Infrastructure
            └── Milvaion.Application
                    └── Milvaion.Domain
```

---

## Component Details

### Milvaion.Domain

Core business entities and logic:

| Component | Description |
|-----------|-------------|
| `ScheduledJob` | Job definition entity |
| `JobOccurrence` | Single execution instance |
| `FailedOccurrence` | DLQ entry for failed jobs |
| `Worker` | Registered worker entity |
| `MilvaionUser` | User entity with roles |
| `OccurrenceStatus` | Enum: Queued, Running, Completed, Failed, etc. |

### Milvaion.Application

Use cases and business logic orchestration:

| Component | Description |
|-----------|-------------|
| **Commands** | Create, Update, Delete operations |
| **Queries** | Read operations with filtering/pagination |
| **DTOs** | Data transfer objects |
| **Validators** | FluentValidation validators |
| **Interfaces** | Repository and service abstractions |
| **Behaviors** | MediatR pipeline behaviors (logging, validation) |

#### CQRS Pattern

```csharp
// Command
public record CreateJobCommand(CreateJobDto Dto) : IRequest<Response<JobDto>>;

// Query
public record GetJobsQuery(ListRequest Request) : IRequest<Response<ListResponse<JobDto>>>;

// Handler
public class CreateJobCommandHandler : IRequestHandler<CreateJobCommand, Response<JobDto>>
{
    public async Task<Response<JobDto>> Handle(CreateJobCommand request, CancellationToken ct)
    {
        // Business logic here
    }
}
```

### Milvaion.Infrastructure

External concerns and implementations:

| Component | Description |
|-----------|-------------|
| **Persistence** | EF Core DbContext, Repositories |
| **Messaging** | RabbitMQ publishers and consumers |
| **Caching** | Redis cache implementations |
| **Scheduling** | Redis ZSET-based scheduling |
| **Services** | External service integrations |

### Milvaion.Api

HTTP API and background services:

| Component | Description |
|-----------|-------------|
| **Controllers** | REST API endpoints |
| **Middlewares** | Request/response processing |
| **Background Services** | Dispatcher, HealthMonitor, etc. |
| **SignalR Hubs** | Real-time dashboard updates |
| **Startup** | DI configuration, middleware pipeline |

---

## Data Flow

### Job Creation Flow

```
1. Client sends POST /api/v1/jobs/job
                    ↓
2. Controller receives request
                    ↓
3. MediatR dispatches CreateJobCommand
                    ↓
4. Validator validates input
                    ↓
5. Handler creates ScheduledJob entity
                    ↓
6. Repository saves to PostgreSQL
                    ↓
7. Scheduler adds to Redis ZSET (next execution time as score)
                    ↓
8. Response returned to client
```

### Job Execution Flow

```
1. Dispatcher polls Redis ZSET for due jobs (score <= now)
                    ↓
2. For each due job:
   a. Create JobOccurrence (status: Queued)
   b. Save to PostgreSQL
   c. Publish message to RabbitMQ
   d. Update job's next execution time in Redis
                    ↓
3. Worker consumes message from RabbitMQ
                    ↓
4. Worker publishes status update (Running)
                    ↓
5. StatusTracker updates occurrence in PostgreSQL
                    ↓
6. Worker executes IJob.ExecuteAsync()
                    ↓
7. Worker publishes status update (Completed/Failed)
   + Logs via LogCollector
                    ↓
8. If failed after max retries → DLQ
                    ↓
9. Dashboard receives SignalR update
```

---
## Database Schema

### Core Tables

```sql
-- Jobs table
CREATE TABLE "ScheduledJobs" (
    "Id" UUID PRIMARY KEY,
    "DisplayName" VARCHAR(200) NOT NULL,
    "Description" TEXT,
    "Tags" VARCHAR(500),
    "WorkerId" VARCHAR(100) NOT NULL,
    "JobType" VARCHAR(200) NOT NULL,
    "JobData" JSONB,
    "CronExpression" VARCHAR(100),
    "ExecuteAt" TIMESTAMPTZ,
    "IsActive" BOOLEAN DEFAULT TRUE,
    "ConcurrentExecutionPolicy" INTEGER DEFAULT 0,
    "TimeoutMinutes" INTEGER,
    "Version" INTEGER DEFAULT 1,
    "CreationDate" TIMESTAMPTZ NOT NULL,
    "CreatorUserName" VARCHAR(100),
    "LastModificationDate" TIMESTAMPTZ,
    "LastModifierUserName" VARCHAR(100)
);

-- Occurrences table
CREATE TABLE "JobOccurrences" (
    "Id" UUID PRIMARY KEY,
    "JobId" UUID NOT NULL REFERENCES "ScheduledJobs"("Id"),
    "CorrelationId" UUID NOT NULL,
    "WorkerId" VARCHAR(100),
    "Status" INTEGER NOT NULL,
    "StartTime" TIMESTAMPTZ,
    "EndTime" TIMESTAMPTZ,
    "DurationMs" BIGINT,
    "Result" TEXT,
    "Exception" TEXT,
    "Logs" JSONB,
    "StatusChangeLogs" JSONB,
    "RetryCount" INTEGER DEFAULT 0,
    "LastHeartbeat" TIMESTAMPTZ,
    "JobVersion" INTEGER,
    "CreatedAt" TIMESTAMPTZ NOT NULL
);

-- Indexes for performance
CREATE INDEX "IX_JobOccurrences_JobId" ON "JobOccurrences"("JobId");
CREATE INDEX "IX_JobOccurrences_Status" ON "JobOccurrences"("Status");
CREATE INDEX "IX_JobOccurrences_CreatedAt" ON "JobOccurrences"("CreatedAt");
CREATE INDEX "IX_ScheduledJobs_WorkerId" ON "ScheduledJobs"("WorkerId");
CREATE INDEX "IX_ScheduledJobs_IsActive" ON "ScheduledJobs"("IsActive");
```

### Entity Relationship Diagram

```
┌─────────────────┐       ┌─────────────────┐
│  ScheduledJobs  │──1:N──│  JobOccurrences │
└─────────────────┘       └─────────────────┘
         │                         │
         │                         │
         └─────────┬───────────────┘
                   │
                   ▼
          ┌──────────────────┐
          │ FailedOccurrences│
          └──────────────────┘
```

---

## Message Queue Design

### Exchange and Queue Structure

```
Exchange: milvaion.job (topic)
??? Queue: milvaion.job.email-worker    (routing: emailworker.#)
??? Queue: milvaion.job.http-worker     (routing: httpworker.#)
??? Queue: milvaion.job.sql-worker      (routing: sqlworker.#)
??? Queue: milvaion.job.custom-worker   (routing: customworker.#)

Exchange: milvaion.status (direct)
??? Queue: milvaion.status              (routing: status)

Exchange: milvaion.logs (direct)
??? Queue: milvaion.logs                (routing: logs)

Exchange: milvaion.dlx (fanout)
??? Queue: milvaion.dlq                 (dead letter queue)
```

### Message Schemas

**Job Message:**
```json
{
  "occurrenceId": "uuid",
  "jobId": "uuid",
  "jobType": "SendEmailJob",
  "jobData": "{ ... }",
  "correlationId": "uuid",
  "timestamp": "2024-01-15T10:30:00Z",
  "retryCount": 0,
  "maxRetries": 3
}
```

**Status Update Message:**
```json
{
  "occurrenceId": "uuid",
  "status": 2,
  "timestamp": "2024-01-15T10:30:05Z",
  "result": "Success",
  "durationMs": 5000
}
```

---

## Caching Strategy

### Redis Key Structure

```
Milvaion:JobScheduler:
├─ schedule                    # ZSET: job schedule (score = next run timestamp)
├─ job:{jobId}                 # HASH: job cache
├─ locks:dispatcher            # STRING: distributed lock for dispatcher
├─ locks:job:{jobId}           # STRING: per-job execution lock
├─ workers                     # HASH: registered workers
├─ heartbeat:{workerId}        # STRING: worker heartbeat timestamp
├─ running:{occurrenceId}      # STRING: running job heartbeat
└─ cancellation_channel        # PUBSUB: job cancellation signals
```

### Caching Layers

1. **Schedule Cache (Redis ZSET)**
   - All active jobs with next execution time as score
   - Dispatcher polls with `ZRANGEBYSCORE`

2. **Job Cache (Redis HASH)**
   - Frequently accessed job details
   - TTL-based expiration

3. **In-Memory Cache (optional)**
   - Configuration data
   - Static lookups

---

## Security Architecture

### Authentication Flow

```
1. Client sends credentials to POST /api/v1/auth/login
                    ↓
2. Validate credentials against database
                    ↓
3. Generate JWT access token + refresh token
                    ↓
4. Return tokens to client
                    ↓
5. Client includes JWT in Authorization header
                    ↓
6. JWT middleware validates token on each request
                    ↓
7. Claims extracted and available in controllers
```

### Authorization Model

- **Role-based access control (RBAC)**
- **Permission-based fine-grained control**
- Roles: Admin, Operator, Viewer

---

## Scalability Patterns

### Horizontal Scaling

**API Servers:**
- Stateless design
- Load balancer distribution
- Sticky sessions for SignalR (or Redis backplane)
- Leader election for dispatcher (only one active)

**Workers:**
- Unlimited horizontal scaling
- RabbitMQ handles distribution
- Each worker has unique instance ID

### Leader Election (Dispatcher)

```csharp
// Pseudo-code for dispatcher leader election
async Task RunDispatcher()
{
    while (true)
    {
        var lockAcquired = await redis.AcquireLock(
            "dispatcher-lock",
            instanceId,
            TimeSpan.FromMinutes(10)
        );
        
        if (lockAcquired)
        {
            // This instance is the leader
            await DispatchDueJobs();
            await redis.ExtendLock("dispatcher-lock", instanceId);
        }
        
        await Task.Delay(1000);
    }
}
```

### Backpressure Handling

- RabbitMQ prefetch count limits concurrent processing
- MaxParallelJobs configuration per worker
- Queue depth monitoring and alerting

---

## Observability Architecture

### Logging Pipeline

```
Application
    ↓
Serilog
    ↓
    ├──> Console (Development)
    ├──> Seq (Production)
    └──> PostgreSQL (Audit logs)
```

### Metrics Pipeline

```
Application
    ↓
OpenTelemetry
    ↓
Prometheus/OTLP Collector
    ↓
Grafana
```

### Tracing

- Distributed tracing with correlation IDs
- OpenTelemetry integration
- Trace propagation across API → RabbitMQ → Worker

---

## Technology Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Database | PostgreSQL | JSONB support, reliability, performance |
| Cache | Redis | Speed, pub/sub, distributed locks, sorted sets |
| Message Queue | RabbitMQ | Reliability, routing, DLQ support, management UI |
| ORM | EF Core | Productivity, migrations, LINQ support |
| API Framework | ASP.NET Core | Performance, ecosystem, cross-platform |
| Real-time | SignalR | Native .NET integration, fallback transports |
| Serialization | System.Text.Json | Performance, native support |
| Logging | Serilog | Structured logging, extensible sinks |
| Validation | FluentValidation | Expressive, testable rules |
| CQRS | MediatR | Clean separation, pipeline behaviors |

---

## Further Reading

- [Configuration Reference](../portaldocs/06-configuration.md)
- [Deployment Guide](../portaldocs/07-deployment.md)
- [Reliability Patterns](../portaldocs/08-reliability.md)
- [Scaling Guide](../portaldocs/09-scaling.md)
