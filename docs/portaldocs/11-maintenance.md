---
id: maintenance
title: Maintenance
sidebar_position: 11
description: Database cleanup, retention policies, and long-term maintenance tasks.
---


# Database Maintenance

This guide covers database cleanup, retention policies, and optimization for Milvaion.

## Why Maintenance Matters

Without cleanup, the `JobOccurrences` table grows continuously:

| Executions | Approximate Size |
|------------|------------------|
| 100,000 | ~250 MB |
| 1,000,000 | ~2.5 GB |
| 10,000,000 | ~25 GB |

Each occurrence stores:
- Status and timestamps
- JSONB logs (can be several KB each)
- Exception details
- Status change history

---

## Automated Cleanup Jobs

Milvaion includes built-in cleanup jobs that run automatically.

### OccurrenceCleanerJob

**Purpose:** Cleans old execution records from `JobOccurrences`

**Schedule:** Every Sunday at 04:00 AM UTC (`0 4 * * 0`)

**Default Retention:**
| Status | Retention |
|--------|-----------|
| Completed | 30 days |
| Failed | 90 days |
| Cancelled | 30 days |
| TimedOut | 90 days |
| Skipped | 30 days |

### ActivityLogCleanerJob

**Purpose:** Cleans old audit logs

**Schedule:** Every 30 days at 02:00 AM UTC

**Retention:** 60 days

### NotificationCleanerJob

**Purpose:** Cleans expired in-app notifications

**Schedule:** Every 5 days at 03:00 AM UTC

**Retention:**
- Seen notifications: 30 days
- Unseen notifications: 60 days

---

## Customizing Retention

### Option 1: Modify SQL Scripts

Edit `src/Milvaion.Api/StaticFiles/SQL/occurrence_cleanup.sql`:

```sql
DO $$
DECLARE
    success_retention_days INTEGER := 7;   -- Changed from 30
    failed_retention_days INTEGER := 30;   -- Changed from 90
    cancelled_retention_days INTEGER := 7; -- Changed from 30
BEGIN
    -- Delete successful occurrences
    DELETE FROM "JobOccurrences"
    WHERE "Status" = 2 -- Completed
      AND ("EndTime" IS NOT NULL 
           AND "EndTime" < NOW() - (success_retention_days || ' days')::INTERVAL);
    
    -- Delete failed occurrences
    DELETE FROM "JobOccurrences"
    WHERE "Status" = 3 -- Failed
      AND ("EndTime" IS NOT NULL 
           AND "EndTime" < NOW() - (failed_retention_days || ' days')::INTERVAL);
    
    -- Delete cancelled occurrences
    DELETE FROM "JobOccurrences"
    WHERE "Status" = 4 -- Cancelled
      AND ("EndTime" IS NOT NULL 
           AND "EndTime" < NOW() - (cancelled_retention_days || ' days')::INTERVAL);
    
    -- Reclaim disk space
    VACUUM ANALYZE "JobOccurrences";
END $$;
```

### Option 2: Change Schedule Frequency

For high-volume systems, run cleanup more frequently:

```csharp
// In InfraServiceCollectionExtensions.cs
services.AddMilvaCronJob<OccurrenceCleanerJob>(c =>
{
    c.TimeZoneInfo = TimeZoneInfo.Utc;
    c.CronExpression = @"0 4 * * *"; // Daily at 4 AM (instead of weekly)
    c.CronFormat = CronFormat.Standard;
});
```

**Recommended schedules:**

| Execution Volume | Schedule | Cron |
|------------------|----------|------|
| < 100K/month | Monthly | `0 4 1 * *` |
| 100K - 1M/month | Weekly | `0 4 * * 0` |
| 1M - 10M/month | Daily | `0 4 * * *` |
| > 10M/month | Twice daily | `0 4,16 * * *` |

---

## Manual Cleanup

### Using API (Safe)

Trigger cleanup manually:

```bash
# This calls the cleanup job
curl -X POST http://localhost:5000/api/v1/admin/maintenance/cleanup
```

### Using SQL (Direct)

For emergency cleanup, run SQL directly:

```sql
-- Delete completed occurrences older than 7 days
DELETE FROM "JobOccurrences"
WHERE "Status" = 2
  AND "EndTime" < NOW() - INTERVAL '7 days';

-- Check how many would be deleted (dry run)
SELECT COUNT(*) FROM "JobOccurrences"
WHERE "Status" = 2
  AND "EndTime" < NOW() - INTERVAL '7 days';

-- Delete all occurrences older than 30 days (any status)
DELETE FROM "JobOccurrences"
WHERE "EndTime" < NOW() - INTERVAL '30 days';

-- Reclaim disk space after large delete
VACUUM FULL "JobOccurrences";
```

> **Warning:** `VACUUM FULL` locks the table. For large tables, use regular `VACUUM` instead.

### Batch Deletion (Large Tables)

For tables with millions of rows, delete in batches:

```sql
-- Delete in batches of 10,000
DO $$
DECLARE
    deleted_count INTEGER;
BEGIN
    LOOP
        DELETE FROM "JobOccurrences"
        WHERE "Id" IN (
            SELECT "Id" FROM "JobOccurrences"
            WHERE "Status" = 2
              AND "EndTime" < NOW() - INTERVAL '7 days'
            LIMIT 10000
        );
        
        GET DIAGNOSTICS deleted_count = ROW_COUNT;
        
        IF deleted_count = 0 THEN
            EXIT;
        END IF;
        
        -- Commit and pause to reduce load
        COMMIT;
        PERFORM pg_sleep(1);
    END LOOP;
END $$;
```

---

## Failed Occurrences Cleanup

Failed occurrences in the `FailedOccurrences` table require manual review. Clean resolved ones:

```sql
-- Delete resolved failed occurrences older than 30 days
DELETE FROM "FailedOccurrences"
WHERE "Resolved" = true
  AND "ResolvedAt" < NOW() - INTERVAL '30 days';

-- Delete old unresolved failures (after 180 days, probably stale)
DELETE FROM "FailedOccurrences"
WHERE "Resolved" = false
  AND "FailedAt" < NOW() - INTERVAL '180 days';
```

---

## Table Partitioning (Advanced)

For very high volumes (>1M occurrences/month), consider partitioning:

### Create Partitioned Table

```sql
-- Create partitioned table by month
CREATE TABLE "JobOccurrences_new" (
    "Id" UUID PRIMARY KEY,
    "JobId" UUID NOT NULL,
    "Status" INTEGER NOT NULL,
    "CreatedAt" TIMESTAMP NOT NULL,
    "EndTime" TIMESTAMP,
    -- ... other columns
) PARTITION BY RANGE ("CreatedAt");

-- Create monthly partitions
CREATE TABLE "JobOccurrences_2025_01" 
PARTITION OF "JobOccurrences_new"
FOR VALUES FROM ('2025-01-01') TO ('2025-02-01');

CREATE TABLE "JobOccurrences_2025_02" 
PARTITION OF "JobOccurrences_new"
FOR VALUES FROM ('2025-02-01') TO ('2025-03-01');
```

### Drop Old Partitions

```sql
-- Much faster than DELETE for large datasets
DROP TABLE "JobOccurrences_2024_01";
```

---

## Indexing

### Required Indexes

Milvaion creates these automatically:

```sql
-- Job ID lookup
CREATE INDEX IX_JobOccurrences_JobId ON "JobOccurrences" ("JobId");

-- Status filtering
CREATE INDEX IX_JobOccurrences_Status ON "JobOccurrences" ("Status");

-- Date range queries
CREATE INDEX IX_JobOccurrences_CreatedAt ON "JobOccurrences" ("CreatedAt" DESC);

-- Cleanup queries
CREATE INDEX IX_JobOccurrences_Status_EndTime 
ON "JobOccurrences" ("Status", "EndTime");
```

### Check for Missing Indexes

```sql
-- Find slow queries
SELECT query, mean_time, calls
FROM pg_stat_statements
WHERE query LIKE '%JobOccurrences%'
ORDER BY mean_time DESC
LIMIT 10;

-- Check index usage
SELECT 
    indexrelname,
    idx_scan,
    idx_tup_read
FROM pg_stat_user_indexes
WHERE schemaname = 'public'
  AND relname = 'JobOccurrences'
ORDER BY idx_scan DESC;
```

---

## Storage Optimization

### JSONB Compression

Logs are stored as JSONB. For large log payloads:

```sql
-- Check average log size per occurrence
SELECT 
    AVG(pg_column_size("Logs")) as avg_log_bytes,
    MAX(pg_column_size("Logs")) as max_log_bytes
FROM "JobOccurrences"
WHERE "Logs" IS NOT NULL;
```

If logs are too large (>10KB average), consider:
1. Reducing log verbosity in jobs
2. Truncating long messages
3. Moving logs to separate table/storage

### Vacuum and Analyze

Schedule regular maintenance:

```sql
-- Update statistics (helps query planner)
ANALYZE "JobOccurrences";
ANALYZE "ScheduledJobs";

-- Reclaim dead tuple space (non-blocking)
VACUUM "JobOccurrences";
```

Or configure autovacuum:

```ini
# postgresql.conf
autovacuum_vacuum_scale_factor = 0.05
autovacuum_analyze_scale_factor = 0.02
autovacuum_vacuum_cost_delay = 10ms
```

---

## Monitoring Storage

### Check Table Sizes

```sql
SELECT 
    relname as table_name,
    pg_size_pretty(pg_total_relation_size(relid)) as total_size,
    pg_size_pretty(pg_relation_size(relid)) as table_size,
    pg_size_pretty(pg_indexes_size(relid)) as index_size
FROM pg_catalog.pg_statio_user_tables
ORDER BY pg_total_relation_size(relid) DESC;
```

### Check Row Counts

```sql
SELECT 
    'JobOccurrences' as table_name,
    COUNT(*) as total_rows,
    COUNT(*) FILTER (WHERE "Status" = 2) as completed,
    COUNT(*) FILTER (WHERE "Status" = 3) as failed,
    COUNT(*) FILTER (WHERE "CreatedAt" > NOW() - INTERVAL '7 days') as last_7_days
FROM "JobOccurrences";
```

### Alert on Growth

```sql
-- Check if table exceeds threshold
SELECT CASE 
    WHEN pg_total_relation_size('"JobOccurrences"') > 5368709120 -- 5GB
    THEN 'ALERT: JobOccurrences exceeds 5GB'
    ELSE 'OK'
END as status;
```

---

## Backup Considerations

### Before Major Cleanup

```bash
# Create backup before large delete operations
pg_dump -h localhost -U milvaion -t JobOccurrences MilvaionDb > backup_occurrences.sql
```

### Exclude Large Tables from Daily Backups

```bash
# Backup without occurrence data (structure only)
pg_dump -h localhost -U milvaion \
  --exclude-table-data='JobOccurrences' \
  MilvaionDb > backup_without_occurrences.sql
```

---

## Maintenance Checklist

### Weekly
- [ ] Check `JobOccurrences` row count
- [ ] Verify cleanup jobs ran successfully
- [ ] Check disk space usage

### Monthly
- [ ] Review failed occurrence backlog
- [ ] Analyze slow query logs
- [ ] Run `VACUUM ANALYZE`

### Quarterly
- [ ] Review retention policies
- [ ] Archive old data if needed
- [ ] Check index health and fragmentation
- [ ] Review storage growth trends

---

## Summary

| Task | Frequency | Method |
|------|-----------|--------|
| Occurrence cleanup | Weekly (auto) | OccurrenceCleanerJob |
| Activity log cleanup | Monthly (auto) | ActivityLogCleanerJob |
| VACUUM ANALYZE | Weekly | pg_cron or manually |
| Check table sizes | Weekly | Monitoring query |
| Index maintenance | Monthly | REINDEX if needed |
| Partition management | Monthly | Drop old partitions |
