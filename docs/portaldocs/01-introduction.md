---
id: introduction
title: Introduction
sidebar_position: 1
description: Overview of Milvaion, its purpose, and core philosophy.
---

# Introduction

Milvaion is a **distributed job scheduling system** built on .NET 10. It separates the *scheduler* (API that decides when jobs run) from the *workers* (processes that execute jobs), connected via Redis and RabbitMQ.

## What Problem Does Milvaion Solve?

Most job schedulers run jobs **inside the same process** as the scheduling logic. This works fine until:

- A long-running job blocks other jobs from executing
- A crashing job takes down the entire scheduler
- You need different hardware for different job types (e.g., GPU for ML jobs)
- You want to scale job execution independently from the API

Milvaion solves these problems by **completely separating scheduling from execution**:

```
┌─────────────────┐        ┌─────────────────┐       ┌─────────────────┐
│  Milvaion API   │        │    RabbitMQ     │       │    Workers      │
│  (Scheduler)    │──────▶│  (Job Queue)    │──────▶│  (Executors)    │
│                 │        │                 │       │                 │
│ • REST API      │        │ • Job messages  │       │ • IJob classes  │
│ • Dashboard     │        │ • Status queues │       │ • Retry logic   │
│ • Cron parsing  │        │ • Log streams   │       │ • DI support    │
└─────────────────┘        └─────────────────┘       └─────────────────┘
```

## When Should You Use Milvaion?

### Good Fit ✅

| Scenario | Why Milvaion Works |
|----------|-------------------|
| **Scheduled background jobs** | Cron-based scheduling with Redis ZSET for precision |
| **High-volume job processing** | Horizontal scaling with RabbitMQ distribution |
| **Long-running jobs (hours)** | Workers isolated from API, no timeout issues |
| **Multi-tenant systems** | Route jobs to specific worker groups |
| **Jobs needing different resources** | GPU workers, high-memory workers, etc. |
| **Compliance/audit requirements** | Full execution history with logs stored in PostgreSQL |

### Not a Good Fit ❌

| Scenario | Better Alternative |
|----------|-------------------|
| **Simple in-process background tasks** | Use `BackgroundService` or Hangfire |
| **Real-time event processing** | Use dedicated event streaming (Kafka, Azure Event Hubs) |
| **Sub-second job scheduling** | Milvaion polls every 1 second minimum |
| **Single-server deployments** | Overhead of Redis + RabbitMQ not justified |
| **Workflow orchestration with branching** | Use Temporal, Elsa, or Azure Durable Functions |

## Core Concepts

| Concept | What It Is |
|---------|-----------|
| **Scheduler (API)** | REST API + background service that reads cron schedules and dispatches jobs |
| **Worker** | A process that consumes jobs from RabbitMQ and executes them |
| **Job** | Represents recurring or one time execution definition |
| **Worker Job** | C# class implementing `IJob` or `IAsyncJob` interface |
| **Occurrence/Execution** | Single execution of a job (has status, logs, duration) |
| **JobData** | JSON payload passed to the job at execution time |

## How It Works

1. **Worker Auto Disvovery** service will auto discover your worker and containing worker jobs.
2. **You create a job according to worker job** via REST API or Dashboard (e.g., "Send daily report at 9 AM")
3. **Scheduler stores it** in PostgreSQL and adds to Redis ZSET with next run time
4. **Dispatcher** checks Redis for due jobs
5. **Due jobs are published** to RabbitMQ with routing key (e.g., `sendreport.*`)
6. **Worker consumes the message**, executes your `IAsyncJob` code
7. **Worker reports back** via RabbitMQ (status updates, logs)
8. **Scheduler persists results** and notifies Dashboard via SignalR

## Key Features

### Reliability
- **At-least-once delivery** via RabbitMQ manual ACK
- **Automatic retries** with exponential backoff
- **Dead Letter Queue** for failed jobs after max retries
- **Zombie detection** recovers stuck jobs
- **Auto disable** always failing jobs (configurable failed execution count).

### Scalability
- **Horizontal worker scaling** - add more workers for more throughput
- **Job-type routing** - route specific jobs to specialized workers
- **Independent scaling** - scale API and workers separately

### Observability
- **Real-time dashboard** with SignalR updates
- **Execution logs**
  - User Friendly Logs -> It is stored within the occurrence to be displayed in the user interface.
  - Technical Logs -> Logs are sent to Seq.
- **Worker health monitoring** via heartbeats
- **OpenTelemetry support** for metrics and tracing

### Developer Experience
- **Simple `IAsyncJob` interface** - implement one method
- **Full DI support** - inject services into jobs
- **Auto-discovery** - jobs registered automatically
- **Cancellation support** - graceful shutdown

## Comparison with Alternatives

| Feature | Milvaion | Hangfire | Quartz.NET |
|---------|----------|----------|------------|
| **Architecture** | Distributed (API + Workers) | Monolithic | Embedded |
| **Worker Isolation** | Separate processes | Same process | Same process |
| **Horizontal Scaling** | Independent | Limited | Complex |
| **Job Dispatching** | RabbitMQ | Database polling | Database polling |
| **Real-time Dashboard** | Built-in | Built-in | None |
| **Log Streaming** | Real-time via RabbitMQ | Console plugin | None |
| **Offline Resilience** | SQLite fallback | None | None |
| **Best For** | Distributed systems | Simple .NET apps | Embedded scheduling |

## Next Steps

- **[Quick Start](02-quick-start.md)** - Run Milvaion locally in 5 minutes
- **[Core Concepts](03-core-concepts.md)** - Understand the architecture
- **[Your First Worker](04-your-first-worker.md)** - Build and deploy a custom worker
