---
id: scaling
title: Scaling
sidebar_position: 9
description: Horizontal and vertical scaling strategies for Milvaion components.
---


# Scaling Guide

This guide covers strategies for scaling Milvaion horizontally and vertically to handle increasing workloads.

## Scaling Overview

Milvaion is designed for horizontal scaling:

| Component | Scaling Type | Strategy |
|-----------|-------------|----------|
| **API Server** | Horizontal | Add more instances behind load balancer |
| **Workers** | Horizontal | Add more instances per job type |
| **PostgreSQL** | Vertical / Read replicas | Larger instance or read replicas |
| **Redis** | Vertical / Cluster | Larger instance or Redis Cluster |
| **RabbitMQ** | Horizontal | Clustering with replicated queues |

---

## Scaling Workers

Workers are **stateless (unless you make them stateful)** – scaling is straightforward.

### Basic Scaling

```bash
# Docker Compose - scale to 5 instances
docker compose up -d --scale email-worker=5

# Kubernetes - scale deployment
kubectl scale deployment email-worker --replicas=5

# Kubernetes HPA (automatic)
kubectl autoscale deployment email-worker --min=2 --max=10 --cpu-percent=70
```

### Capacity Planning

**Jobs per second = Workers × MaxParallelJobs × (1 / AvgJobDuration)**

Example:

* 3 workers
* MaxParallelJobs: 10
* Average job duration: 2 seconds

```
Throughput = 3 × 10 × (1 / 2) = 15 jobs/second = 900 jobs/minute
```

### Specialized Workers

Create job-specific workers for resource optimization:

**Email Worker (I/O-bound, high concurrency)**

```json
{
  "Worker": {
    "WorkerId": "email-worker",
    "MaxParallelJobs": 100
  }
}
```

**Report Worker (CPU-bound, low concurrency)**

```json
{
  "Worker": {
    "WorkerId": "report-worker",
    "MaxParallelJobs": 4
  }
}
```

### Worker Affinity

Route specific jobs to specific worker pools:

```
+---------------------+
|      RabbitMQ       |
|    Topic Exchange   |
+---------------------+
            |
   -------------------------
   |           |           |
sendemail.*  report.*   migration.*
   |           |           |
+---------+ +---------+ +------------+
| Email   | | Report  | | Migration  |
| Workers | | Workers | | Worker     |
| (x10)   | | (x2)    | | (x1)        |
+---------+ +---------+ +------------+
```

---

## Scaling API Server

### Horizontal Scaling

The API is **stateless** – scale by adding instances:

```yaml
# docker-compose.yml
services:
  milvaion-api:
    image: milvasoft/milvaion-api:latest
    deploy:
      replicas: 3
```

### Load Balancer Configuration

**NGINX example:**

```nginx
upstream milvaion_api {
    least_conn;
    server api-1:5000;
    server api-2:5000;
    server api-3:5000;
}

server {
    listen 80;

    location / {
        proxy_pass http://milvaion_api;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
    }
}
```

> **Note**: Enable WebSocket support for SignalR (`Upgrade` and `Connection` headers).

### Dispatcher Leader Election

Only **one dispatcher** should be active. Milvaion uses Redis distributed locking:

```
1. Each API instance attempts to acquire dispatcher lock
2. Lock winner runs dispatcher service
3. Other instances skip dispatch (passive standby)
4. If leader fails, lock expires, another instance takes over
```

Configure lock TTL:

```json
{
  "MilvaionConfig": {
    "JobDispatcher": {
      "LockTtlSeconds": 600
    }
  }
}
```

---

## Scaling Infrastructure

### PostgreSQL

**Vertical Scaling (Primary):**

| Workload | vCPU | RAM   | Storage    |
| -------- | ---- | ----- | ---------- |
| Small    | 2    | 4 GB  | 50 GB SSD  |
| Medium   | 4    | 8 GB  | 100 GB SSD |
| Large    | 8    | 16 GB | 500 GB SSD |

**Read Replicas:**

For heavy read workloads (dashboard, reports), add read replicas.

### Redis

**Vertical Scaling:**

| Workload | RAM   | Notes                            |
| -------- | ----- | -------------------------------- |
| Small    | 1 GB  | Single instance                  |
| Medium   | 4 GB  | Single instance with persistence |
| Large    | 8+ GB | Redis Cluster or Redis Sentinel  |

**Key Capacity Planning:**

* ~1 KB per scheduled job
* ~100 bytes per worker heartbeat
* ~500 bytes per distributed lock

**Example:** 10,000 active jobs ≈ 10 MB

### RabbitMQ

**Clustering:**

For high availability, use RabbitMQ clustering with quorum queues.

**Queue Capacity:**

* ~500 bytes per queued job message
* 100,000 pending jobs ≈ 50 MB

---

## Throughput Benchmarks

### Reference Numbers

Tested on: 4 vCPU, 8 GB RAM, PostgreSQL / Redis / RabbitMQ on same machine

| Scenario            | Jobs/sec | Workers | Concurrency |
| ------------------- | -------- | ------- | ----------- |
| Simple logging job  | 500      | 1       | 50          |
| API call (100 ms)   | 100      | 1       | 10          |
| Database insert     | 200      | 1       | 20          |
| Email send (500 ms) | 20       | 1       | 10          |

**Scaling linearly with workers:**

* 10 workers × 20 jobs/sec = 200 jobs/sec

### Bottleneck Identification

| Symptom                      | Likely Bottleneck           | Solution                    |
| ---------------------------- | --------------------------- | --------------------------- |
| High API CPU                 | Too many dashboard polls    | Add API replicas            |
| Redis high latency           | Too many ZSET operations    | Redis Cluster               |
| RabbitMQ queue depth growing | Workers too slow            | Add workers                 |
| PostgreSQL high CPU          | Too many occurrence inserts | Read replicas, partitioning |
| Worker high memory           | Job data too large          | Optimize job payloads       |

---

## Concurrency Policies

### Per-Job Concurrency

```json
{
  "concurrentExecutionPolicy": 0
}
```

| Value | Policy    | Behavior                                         |
| ----- | --------- | ------------------------------------------------ |
| 0     | **Skip**  | Do not create occurrence if already running      |
| 1     | **Queue** | Create occurrence, wait for previous to complete |

### Worker-Level Concurrency

```json
{
  "Worker": {
    "MaxParallelJobs": 10
  },
  "JobConsumers": {
    "SendEmailJob": { "MaxParallelJobs": 20 },
    "GenerateReportJob": { "MaxParallelJobs": 2 }
  }
}
```

---

## Auto-Scaling Patterns

### Kubernetes HPA

```yaml
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: email-worker-hpa
spec:
  scaleTargetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: email-worker
  minReplicas: 2
  maxReplicas: 20
  metrics:
    - type: Resource
      resource:
        name: cpu
        target:
          type: Utilization
          averageUtilization: 70
```

### Queue-Based Scaling (KEDA)

```yaml
apiVersion: keda.sh/v1alpha1
kind: ScaledObject
metadata:
  name: email-worker-scaler
spec:
  scaleTargetRef:
    name: email-worker
  minReplicaCount: 1
  maxReplicaCount: 50
  triggers:
    - type: rabbitmq
      metadata:
        queueName: jobs.sendemail.wildcard
        host: amqp://guest:guest@rabbitmq:5672/
        mode: QueueLength
        value: "100"
```

---

## Scaling Checklist

### Before Scaling

* Identify the bottleneck (CPU, memory, I/O, network)
* Measure current throughput baseline
* Check infrastructure capacity (connections, disk)

### After Scaling

* Verify even load distribution
* Monitor for new bottlenecks
* Update connection pool sizes if needed
* Adjust timeout values for increased load

### Connection Limits

| Component  | Default Limit      | Recommendation          |
| ---------- | ------------------ | ----------------------- |
| PostgreSQL | 100 connections    | Increase to workers × 2 |
| Redis      | 10,000 connections | Usually sufficient      |
| RabbitMQ   | 65,535 connections | Usually sufficient      |

---

## What's Next?

* **[Monitoring](10-monitoring.md)** – Metrics and alerting
* **[Database Maintenance](11-maintenance.md)** – Cleanup and retention

