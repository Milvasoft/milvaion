---
id: scheduling
title: "Scheduling Options"
sidebar_label: "Scheduling Options"
sidebar_position: 56
description: "How Milvaion decides when a job runs: cron expressions, time zones, missed runs after downtime, priority and exclusion calendars."
keywords: [milvaion scheduling, cron, missed runs, catch-up, misfire, job priority, exclusion calendar]
---

# Scheduling Options

Every job has a schedule: either a cron expression for a recurring job or a single time for a one-time job. On top of that, each job has four settings that control exactly when it runs:

| Setting | Default | What it does |
|---------|---------|--------------|
| Time zone | `UTC` | The zone the cron expression is read in. See [Time Zones](29-time-zones.md). |
| Missed runs | `RunOnce` | What happens to runs missed while the scheduler was down. |
| Priority | `0` | Which jobs are sent first when several are due at once. |
| Exclusion calendars | none | Days on which the job does not run. See [Exclusion Calendars](30-exclusion-calendars.md). |

The defaults reproduce how jobs behaved before these settings existed, so existing jobs run exactly as they did. You set them on the job form in the dashboard, through the API (`POST /api/v1/jobs/job`, `PUT /api/v1/jobs/job`) or with the MCP tools `create_job` and `update_job`.

## Cron expressions

Cron expressions have six fields, seconds first:

```
┌─ second (0-59)
│ ┌─ minute (0-59)
│ │ ┌─ hour (0-23)
│ │ │ ┌─ day of month (1-31)
│ │ │ │ ┌─ month (1-12)
│ │ │ │ │ ┌─ day of week (0-6, Sunday = 0)
0 0 9 * * MON-FRI     every weekday at 09:00
0 */15 * * * *        every 15 minutes
0 30 2 1 * *          02:30 on the first of every month
```

The expression is read as wall clock time in the job's time zone.

## Missed runs

If the scheduler is down - a deploy, a crash, a restart, or the emergency stop on the dashboard - the runs due in that time are not dispatched on time. When the scheduler comes back, the job's **missed-run policy** decides what happens to them:

| Policy | Behaviour |
|--------|-----------|
| `RunOnce` (default) | One catch-up run is executed for all of them, then the job continues at its next scheduled time. This is how missed runs behaved before this setting existed. |
| `Skip` | The missed runs are not executed. One occurrence with the status **Skipped** is recorded for them, and the job continues at its next scheduled time. |
| `RunAll` | Each missed run is executed, one after another, up to the job's **maximum catch-up runs** (1 to 100, default 10). If more runs were missed, the oldest are dropped and only the most recent ones run. |

Catch-up runs are normal runs: they respect the job's concurrency policy and its worker's capacity, so `RunAll` with the Skip concurrency policy runs them one at a time. Each catch-up occurrence has a log line from the scheduler that starts with "Catch-up run" and lists how many runs were missed, between which times, and how many were dropped.

### What counts as missed

A run counts as missed when both of these are true:

- It is a **misfire**: it is still waiting more than 60 seconds after its scheduled time. This is the same rule the `JobMisfired` alert uses.
- Its scheduled time falls in a period in which **no dispatcher was polling**. Every API instance records a heartbeat in Redis on each poll; a gap longer than 60 seconds between two polls is stored as an outage.

A run that is late for any other reason - the previous run is still going and the concurrency policy is Skip, or the worker is at capacity - is not missed. It runs late, as it always has.

One-time jobs are never dropped by this policy. A one-time job that was due during an outage runs when the scheduler comes back.

## Priority

Priority is a number from 0 (lowest, the default) to 9 (highest). When several jobs are due in the same dispatcher cycle, higher priority jobs are locked, recorded and published first; jobs with the same priority go in the order they became due.

The priority is also sent in the AMQP `priority` property of each job message. Worker queues are not RabbitMQ priority queues yet, so the broker itself does not reorder messages that are already queued. Priority therefore decides the order in which the scheduler sends jobs, not the order in which a busy worker takes them from its queue. The [worker protocol](https://github.com/Milvasoft/milvaion/blob/master/docs/githubdocs/WORKER-PROTOCOL.md) has the details.

## Exclusion calendars

An exclusion calendar is a named set of dates, date ranges and weekdays. A job can reference any number of them. A run that falls on an excluded day - in the job's own time zone - is not dispatched; it is recorded with the status **Skipped**, and the job continues at its first run on a day that is not excluded. See [Exclusion Calendars](30-exclusion-calendars.md).

## Where to see the result

- **Upcoming Executions** lists the next run of every job with its time zone and local time. Runs on excluded days are left out; when a job's next run falls on an excluded day, the list shows the first run after it and marks it "after excluded day".
- **Job detail** shows the zone, missed-run policy, priority and calendars of the job.
- **Executions** shows skipped runs with the status Skipped and the reason in the result.
