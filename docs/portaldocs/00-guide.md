---
id: milvaion-doc-guide
title: Documentation Guide
sidebar_position: 0
description: Documentation guide of Milvaion.
---


# Milvaion Documentation

Welcome to the official Milvaion documentation. This documentation will help you understand, set up, and operate Milvaion - a distributed job scheduling system.

## Documentation Structure

### Getting Started

| Document | Description |
|----------|-------------|
| [01-introduction.md](01-introduction.md) | What is Milvaion, when to use it, comparison with alternatives |
| [02-quick-start.md](02-quick-start.md) | Get running locally in under 10 minutes |
| [03-core-concepts.md](03-core-concepts.md) | Understand the architecture and key terms |

### Developer Guide

| Document | Description |
|----------|-------------|
| [04-your-first-worker.md](04-your-first-worker.md) | Create and deploy a custom worker |
| [05-implementing-jobs.md](05-implementing-jobs.md) | Write jobs with DI, error handling, testing |
| [06-configuration.md](06-configuration.md) | All configuration options for API and Workers |
| [14-built-in-workers.md](14-built-in-workers.md) | Pre-built workers (HTTP Worker, etc.) |

### Operations Guide

| Document | Description |
|----------|-------------|
| [07-deployment.md](07-deployment.md) | Production deployment with Docker and Kubernetes |
| [08-reliability.md](08-reliability.md) | Retry, DLQ, zombie detection, idempotency |
| [09-scaling.md](09-scaling.md) | Horizontal scaling strategies |
| [10-monitoring.md](10-monitoring.md) | Health checks, metrics, logging, alerting |
| [11-maintenance.md](11-maintenance.md) | Database cleanup and retention policies |

## Quick Links

- **First time→** Start with [Introduction](01-introduction.md)
- **Want to run it→** Jump to [Quick Start](02-quick-start.md)
- **Building a worker→** See [Your First Worker](04-your-first-worker.md)
- **Using built-in workers→** See [Built-in Workers](14-built-in-workers.md)
- **Going to production→** Read [Deployment](07-deployment.md)

## Reading Order

For new users, we recommend reading in this order:

```
1. Introduction      → Understand what Milvaion is
2. Quick Start       → Get it running locally
3. Core Concepts     → Understand the architecture
4. Your First Worker → Build something real
5. Configuration     → Reference as needed
6. (Optional) Reliability, Scaling, Monitoring for production
```

## Version

This documentation covers **Milvaion 1.0.0** with:
- .NET 10
- PostgreSQL 16
- Redis 7
- RabbitMQ 3.x

## Feedback

Found an issue or want to suggest improvements? Open an issue on GitHub.
