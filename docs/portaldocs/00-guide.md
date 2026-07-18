---
id: milvaion-doc-guide
title: Documentation Guide
sidebar_position: 0
description: Documentation guide of Milvaion.
---

<p align="center">
  <img src={require('./src/logo256.png').default} alt="Milvaion" />
</p>

# Milvaion Documentation

Welcome to the official Milvaion documentation. This documentation will help you understand, set up, and operate Milvaion - a distributed job scheduling system.

## Documentation Structure

### Getting Started

| Document | Description |
|----------|-------------|
| [Introduction](01-introduction.md) | What is Milvaion, when to use it, comparison with alternatives |
| [Quick Start](02-quick-start.md) | Get running locally in under 10 minutes |
| [Core Concepts](03-core-concepts.md) | Understand the architecture and key terms |

### Developer Guide

| Document | Description |
|----------|-------------|
| [Your First Worker](04-your-first-worker.md) | Create and deploy a custom worker |
| [Implementing Jobs](05-implementing-jobs.md) | Write jobs with DI, error handling, testing |
| [Configuration](06-configuration.md) | All configuration options for API and Workers |
| [Testing](23-testing.md) | Unit test jobs locally without RabbitMQ, Redis, or a live environment |

### Operations Guide

| Document | Description |
|----------|-------------|
| [Deployment](07-deployment.md) | Production deployment with Docker and Kubernetes |
| [Reliability](08-reliability.md) | Retry, DLQ, zombie detection, idempotency |
| [Scaling](09-scaling.md) | Horizontal scaling strategies |
| [Monitoring](10-monitoring.md) | Health checks, metrics, logging, alerting |
| [Maintenance](11-maintenance.md) | Database cleanup and retention policies |
| [Built-in Workers](./built-in-workers/14-http-worker.md) | Pre-built workers (HTTP Worker, SQL Worker, Email Worker, Maintenance Worker) |
| [Workflows](20-workflows.md) | Build multi-step job pipelines with DAG-based orchestration |
| [Api Keys](24-api-keys.md) | Credentials for CI pipelines, scripts and MCP clients |
| [MCP Server](25-mcp-server.md) | Connect Claude Code, Cursor or Copilot and ask about your jobs |


## Quick Links

- **First time →** Start with [Introduction](01-introduction.md)
- **Want to run it →** Jump to [Quick Start](02-quick-start.md)
- **Building a worker →** See [Your First Worker](04-your-first-worker.md)
- **Going to production →** Read [Deployment](07-deployment.md)
- **Security considerations →** Read [Security](12-security.md)
- **Milvaion UI Features →** See [UI Screenshots](13-dashboard-screenshots.md)
- **Built-in Workers →** See [Built-in Workers](./built-in-workers/14-http-worker.md)
- **Workflows →** See [Workflows](20-workflows.md)
- **Using it from an AI assistant →** See [MCP Server](25-mcp-server.md)
- **CI or script access →** See [Api Keys](24-api-keys.md)

## Reading Order

For new users, we recommend reading in this order:

```
1. Introduction      → Understand what Milvaion is
2. Quick Start       → Get it running locally
3. Core Concepts     → Understand the architecture
4. Your First Worker → Build something real
5. Configuration     → Reference as needed
6. (Optional) Reliability, Scaling, Monitoring, Security for production, Built-in workers
```

## Version

This documentation covers **Milvaion 1.0.0** with:
- .NET 10
- PostgreSQL 16
- Redis 7
- RabbitMQ 3.x

## Feedback

Found an issue or want to suggest improvements? Open an issue on [GitHub](https://github.com/Milvasoft/milvaion).
