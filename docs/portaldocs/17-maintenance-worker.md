---
id: maintenance-worker
title: Maintenance Worker
sidebar_position: 17
description: Built-in Maintenance Worker for scheduler system housekeeping tasks.
---

# Maintenance Worker

The **MilvaionMaintenance** worker provides essential housekeeping jobs for the Milvaion Job Scheduler system. It handles database cleanup, Redis cache management, and data archival.

## Overview

| Job | Purpose | Recommended Schedule |
|-----|---------|---------------------|
| `DatabaseMaintenanceJob` | VACUUM, ANALYZE, REINDEX | Weekly (Sunday 03:00) |
| `OccurrenceRetentionJob` | Delete old occurrences | Daily (02:00) |
| `FailedOccurrenceCleanupJob` | Clean DLQ table | Weekly |
| `RedisCleanupJob` | Remove orphaned cache entries | Daily |
| `OccurrenceArchiveJob` | Archive old occurrences to dated tables | Monthly (1st, 04:00) |

:::note
**Zombie/Stale detection** is handled by the Scheduler API's built-in background services (`ZombieOccurrenceDetectorService` and `WorkerHealthMonitorService`), not by this worker.
:::

## Jobs

### DatabaseMaintenanceJob

Performs PostgreSQL maintenance operations to keep the database performant.

**Operations:**
- **VACUUM** - Reclaims storage from deleted/updated rows
- **ANALYZE** - Updates query planner statistics
- **REINDEX** - Rebuilds indexes (use with caution, locks tables)

**Configuration:**
```json
"DatabaseMaintenance": {
  "EnableVacuum": true,
  "EnableAnalyze": true,
  "EnableReindex": false,
  "Tables": [
    "JobOccurrences",
    "ScheduledJobs",
    "FailedOccurrences"
  ]
}
```

**Cron:** `0 3 * * 0` (Every Sunday at 03:00)

---

### OccurrenceRetentionJob

Deletes old job occurrences based on status-specific retention policies.

**Retention Policy:**
| Status | Default Retention |
|--------|-------------------|
| Completed | 30 days |
| Failed | 90 days |
| Cancelled | 30 days |
| TimedOut | 30 days |

**Configuration:**
```json
"OccurrenceRetention": {
  "CompletedRetentionDays": 30,
  "FailedRetentionDays": 90,
  "CancelledRetentionDays": 30,
  "TimedOutRetentionDays": 30,
  "BatchSize": 1000
}
```

**Cron:** `0 2 * * *` (Every day at 02:00)

---

### FailedOccurrenceCleanupJob

Cleans up old entries from the `FailedOccurrences` table (Dead Letter Queue).

**Configuration:**
```json
"FailedOccurrenceRetention": {
  "RetentionDays": 180,
  "BatchSize": 500
}
```

**Cron:** `0 4 * * 0` (Every Sunday at 04:00)

---

### RedisCleanupJob

Removes orphaned Redis entries that are no longer needed:
- **Orphaned job cache** - Cache for deleted jobs
- **Stale locks** - Lock entries without TTL
- **Orphaned running states** - Running states for inactive jobs

**Configuration:**
```json
"RedisCleanup": {
  "KeyPrefix": "Milvaion:JobScheduler:",
  "CleanOrphanedJobCache": true,
  "CleanStaleLocks": true,
  "CleanOrphanedRunningStates": true,
  "StaleLockHours": 24
}
```

**Cron:** `0 5 * * *` (Every day at 05:00)

---

### OccurrenceArchiveJob

Archives old occurrences to dated tables instead of deleting them. Useful for compliance and auditing.

**How it works:**
1. Creates archive table: `JobOccurrences_Archive_2024_01`
2. Moves old occurrences to archive table
3. Optionally creates indexes on archive table

**Configuration:**
```json
"OccurrenceArchive": {
  "ArchiveAfterDays": 90,
  "ArchiveTablePrefix": "JobOccurrences_Archive",
  "StatusesToArchive": [2, 3, 4, 5],
  "BatchSize": 1000,
  "CreateIndexOnArchive": true
}
```

**Cron:** `0 4 1 * *` (1st day of month at 04:00)

**Archive Tables Created:**
```
JobOccurrences_Archive_2024_01
JobOccurrences_Archive_2024_02
JobOccurrences_Archive_2024_03
...
```

## Full Configuration

```json
{
  "MaintenanceConfig": {
    "DatabaseConnectionString": "Host=postgres;Database=MilvaionDb;...",
    "RedisConnectionString": "redis:6379",
    "OccurrenceRetention": {
      "CompletedRetentionDays": 30,
      "FailedRetentionDays": 90,
      "CancelledRetentionDays": 30,
      "TimedOutRetentionDays": 30,
      "BatchSize": 1000
    },
    "FailedOccurrenceRetention": {
      "RetentionDays": 180,
      "BatchSize": 500
    },
    "DatabaseMaintenance": {
      "EnableVacuum": true,
      "EnableAnalyze": true,
      "EnableReindex": false,
      "Tables": ["JobOccurrences", "ScheduledJobs", "FailedOccurrences"]
    },
    "RedisCleanup": {
      "KeyPrefix": "Milvaion:JobScheduler:",
      "CleanOrphanedJobCache": true,
      "CleanStaleLocks": true,
      "CleanOrphanedRunningStates": true,
      "StaleLockHours": 24
    },
    "OccurrenceArchive": {
      "ArchiveAfterDays": 90,
      "ArchiveTablePrefix": "JobOccurrences_Archive",
      "StatusesToArchive": [2, 3, 4, 5],
      "BatchSize": 1000,
      "CreateIndexOnArchive": true
    }
  }
}
```

## Deployment

### Docker Compose

```yaml
services:
  maintenance-worker:
    image: milvasoft/milvaion-maintenance-worker:latest
    environment:
      - Worker__WorkerId=maintenance-worker-01
      - Worker__RabbitMQ__Host=rabbitmq
      - Worker__Redis__ConnectionString=redis:6379
      - MaintenanceConfig__DatabaseConnectionString=Host=postgres;...
      - MaintenanceConfig__RedisConnectionString=redis:6379
    depends_on:
      - rabbitmq
      - redis
      - postgres
    restart: unless-stopped
```

### Environment Variables

All configuration can be overridden via environment variables:

```bash
# Database
MaintenanceConfig__DatabaseConnectionString=Host=...

# Retention
MaintenanceConfig__OccurrenceRetention__CompletedRetentionDays=30
MaintenanceConfig__OccurrenceRetention__FailedRetentionDays=90
```

## Scheduling Jobs

After deploying the worker, create scheduled jobs in the UI:

| Job | Cron Expression | Description |
|-----|-----------------|-------------|
| DatabaseMaintenanceJob | `0 3 * * 0` | Sunday 03:00 |
| OccurrenceRetentionJob | `0 2 * * *` | Daily 02:00 |
| FailedOccurrenceCleanupJob | `0 4 * * 0` | Sunday 04:00 |
| RedisCleanupJob | `0 5 * * *` | Daily 05:00 |
| OccurrenceArchiveJob | `0 4 1 * *` | Monthly 1st 04:00 |

## Job Results

All jobs return JSON results for monitoring:

```json
{
  "Success": true,
  "TotalDeleted": 1523,
  "Details": {
    "Completed": 1200,
    "Failed": 200,
    "Cancelled": 123
  }
}
```

## Best Practices

1. **Schedule during low traffic** - Run maintenance jobs during off-peak hours
2. **Monitor execution time** - Adjust batch sizes if jobs take too long
3. **Use archive for compliance** - Keep `OccurrenceArchiveJob` if you need audit trails
4. **Delete or archive, not both** - Choose one retention strategy
5. **Monitor disk space** - Archive tables can grow; drop old ones periodically

## Choosing Retention vs Archive

| Scenario | Use |
|----------|-----|
| No compliance requirements | `OccurrenceRetentionJob` (delete) |
| Need audit trail | `OccurrenceArchiveJob` (archive) |
| Both | Archive first, then delete very old archives |

---

*For custom workers, see [Your First Worker](04-your-first-worker.md).*
