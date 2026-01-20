---
id: dashboard-screenshots
title: Dashboard Screenshots
sidebar_position: 13
description: Visual guide to the Milvaion Dashboard interface and features.
---

# Dashboard Screenshots

This page provides a visual tour of the Milvaion Dashboard, showcasing its key features and interfaces.

---

## Overview

The main dashboard provides a comprehensive view of your job scheduling system at a glance.

![Dashboard Overview](./images/dashboard-overview.png)

**Key elements:**
- Total executions and success rate
- Active workers and capacity utilization
- Queued, running, and completed jobs
- Executions per minute metrics
- Quick access to recent activity

---

## Jobs Management

### Job List View

Browse all scheduled jobs with filtering and sorting capabilities.

![Jobs List](./images/jobs-list.png)

**Features:**
- Filter by status (Active, Disabled, Deleted)
- Search by job name or type
- Sort by next run time, last execution, or creation date
- Quick actions (Enable/Disable, Trigger, Edit, Delete)

### Job Details

View comprehensive information about a specific job.

![Job Details](./images/job-details.png)

**Information displayed:**
- Job configuration (cron expression, timeout, retries)
- Execution history chart
- Recent occurrences with status
- Job data payload (JSON)

### Create / Edit Job

Create new jobs or modify existing ones through an intuitive form.

![Create Job](./images/job-create.png)

![Edit Job](./images/job-edit.png)

**Configuration options:**
- Job type selection (Worker Job Types)
- Cron expression builder with preview
- Job data (JSON payload)
- Timeout and retry settings
- Enable/Disable toggle

---

## Occurrences

### Occurrence List

View all job executions with detailed status information.

![Occurrences List](./images/occurrences-list.png)

**Columns:**
- Job Name and Type
- Status (Queued, Running, Completed, Failed, Cancelled, TimedOut)
- Start Time and Duration
- Worker Instance
- Actions (View Logs, Cancel)

### Occurrence Details

Drill down into a specific execution for troubleshooting.

![Occurrence Details](./images/occurrence-details.png)

**Details included:**
- Execution timeline
- User-friendly logs
- Status change history
- Error details and stack trace (for failed jobs)
- Job data snapshot

### Occurrence Logs

View execution logs for debugging and monitoring.

![Occurrence Logs](./images/occurrence-logs.png)

---

## Workers

### Worker List

Monitor all registered workers and their health status.

![Workers List](./images/workers-list.png)

**Information displayed:**
- Worker ID and Instance ID
- Status (Active, Inactive, Unhealthy)
- Last heartbeat time
- Current job count / Max capacity
- Supported job types

### Worker Details

View detailed information about a specific worker instance.

![Worker Details](./images/worker-details.png)

**Details included:**
- Worker metadata (OS, runtime version, processor count)
- Job configurations (parallelism, timeouts per job type)
- Active jobs on this worker
- Heartbeat history

---

## Real-Time Updates

The dashboard uses SignalR for live updates without page refresh.

![Real-Time Updates](./images/realtime-updates.png)

**Live updates for:**
- Job status changes
- New occurrences
- Worker heartbeats
- Queue depth changes
- Execution completion notifications

---

## Statistics & Charts

### Execution Statistics

View execution metrics over time.

![Execution Stats](./images/stats-executions.png)

**Charts available:**
- Executions per day/hour
- Success vs failure rate
- Average duration trends
- Peak execution times

### Status Distribution

Analyze job execution outcomes.

![Status Distribution](./images/stats-status-distribution.png)

---

## Activity Log

Track all system activities and user actions.

![Activity Log](./images/activity-log.png)

**Logged activities:**
- Job created/updated/deleted
- Job triggered manually
- Job enabled/disabled
- Occurrence cancelled
- Worker registered/unregistered

---

## Settings

### System Configuration

View and manage system settings.

![Settings](./images/settings.png)

**Configurable options:**
- Dispatcher settings
- Health monitor configuration
- Auto-disable thresholds
- Retention policies

---

## User Management

### Users List

Manage dashboard users and their permissions.

![Users List](./images/users-list.png)

### Roles & Permissions

Configure role-based access control.

![Roles](./images/roles-permissions.png)

---

## Mobile Responsive

The dashboard is fully responsive and works on mobile devices.

![Mobile View](./images/mobile-view.png)

---

## Dark Mode

Toggle between light and dark themes.

![Dark Mode](./images/dark-mode.png)

---

## Related Documentation

- [Introduction](./01-introduction.md)
- [Quick Start](./02-quick-start.md)
- [Monitoring](./10-monitoring.md)
