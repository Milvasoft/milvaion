---
id: introduction
title: Introduction
sidebar_position: 10
description: Overview of Milvaion, its purpose, and core philosophy.
---

<p align="center">
  <img src={require('./src/logo256.png').default} alt="Milvaion" />
</p>

# Introduction

Milvaion is a **distributed job scheduling system** built on .NET 10. It separates the *scheduler* (API that decides when jobs run) from the *workers* (processes that execute jobs), connected via Redis and RabbitMQ.

:::tip Already using Hangfire or Quartz.NET?

You do not have to migrate. Milvaion plugs into your existing scheduler in **two lines of code** and gives you a unified dashboard, execution history, metrics and alerting across every service — without touching your job code.

**→ [Hangfire & Quartz.NET Integration](./18-external-scheduler.md)**

:::

## What Problem Does Milvaion Solve?

Most job schedulers run jobs **inside the same process** as the scheduling logic by default. This works fine until:

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
| **Multi-step pipelines with branching** | DAG-based [Workflows](./20-workflows.md) with conditions, merges and data mappings |
| **Existing Hangfire/Quartz.NET estates** | [Integrate without migrating](./18-external-scheduler.md) — monitor every scheduler in one dashboard |

### Not a Good Fit ❌

| Scenario | Better Alternative |
|----------|-------------------|
| **Simple in-process background tasks** | Use `BackgroundService` or Hangfire — you can still [add Milvaion monitoring](./18-external-scheduler.md) on top |
| **Real-time event processing** | Use dedicated event streaming (Kafka, Azure Event Hubs) |
| **Sub-second job scheduling** | Milvaion polls every 1 second minimum |
| **Single-server deployments** | Overhead of Redis + RabbitMQ not justified |

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

1. **Worker Auto Discovery** service will auto discover your worker and containing worker jobs.
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
- **Alerting** - configurable notifications for job failures and system events via multiple channels (Google Chat, Microsoft Teams, Slack, Email and in-app)

### Developer Experience
- **Simple `IAsyncJob` interface** - implement one method
- **Full DI support** - inject services into jobs
- **Auto-discovery** - jobs registered automatically
- **Cancellation support** - graceful shutdown

### AI Integration
- **[MCP server](./25-mcp-server.md)** - connect Claude Code, Cursor or GitHub Copilot and ask about your jobs in plain language
- **32 tools** - reading, triggering, pausing, editing and deleting, each behind its own permission
- **Scoped by api key** - a read-only key lets an assistant investigate everything and change nothing
- **No model provider keys** - Milvaion is the data source; the model runs in the user's own editor

### Enterprise Management
- **Role-based access control** - define roles with specific permissions
- **User management** - create and manage users with assigned roles
- **Permission granularity** - control access at job, worker, and dashboard level
- **[Api keys](./24-api-keys.md)** - non-interactive credentials for CI pipelines, scripts and MCP clients

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
| **Workflow Engine (DAG)** | Built-in, visual builder | Continuations only | None |
| **MCP server for AI assistants** | Built-in, 32 tools | None | None |
| **Monitors the others** | ✅ Hangfire + Quartz.NET | N/A | N/A |
| **Best For** | Distributed systems | Simple .NET apps | Embedded scheduling |

:::note Not an either/or

The last row is the important one. Milvaion is not only a Hangfire or Quartz.NET replacement — it also **monitors them**. If you already run either, start with [the integration](./18-external-scheduler.md) and decide about migration later.

:::

## Next Steps

- **[Hangfire & Quartz.NET Integration](./18-external-scheduler.md)** - Already have a scheduler? Start here
- **[MCP Server](./25-mcp-server.md)** - Ask your scheduler questions from Claude Code, Cursor or Copilot
- **[Quick Start](02-quick-start.md)** - Run Milvaion locally in 5 minutes
- **[Core Concepts](03-core-concepts.md)** - Understand the architecture
- **[Your First Worker](04-your-first-worker.md)** - Build and deploy a custom worker
