---
id: workflows
title: Workflows
sidebar_position: 55
description: Build multi-step job pipelines with conditional branching, data passing, and DAG-based orchestration using Milvaion Workflows.
---

# Workflows

Workflows let you chain multiple jobs into a **directed acyclic graph (DAG)** where each step runs a scheduled job and the engine coordinates execution order, data passing, conditional branching, and failure handling automatically.

## What Is a Workflow?

A workflow is a named, versionable pipeline made up of:

| Concept | Description |
|---------|-------------|
| **Step** | A node in the DAG — either a Job (Task), a Condition, or a Merge point |
| **Edge** | A directed connection from one step to another |
| **Run** | A single execution instance of the workflow |
| **Data Mapping** | A rule that passes an output field from one step as an input to another |

---

## Core Concepts

### Node Types

Every step in a workflow is one of three node types:

| Type | Description |
|------|-------------|
| **Task** |  Dispatches a scheduled job. This is the primary building block. |
| **Condition** |  Evaluates an expression and routes execution through a `true` or `false` port. Does not run a job. |
| **Merge** | Waits for all incoming branches to complete before allowing downstream steps to proceed. Does not run a job. |

> **Condition** and **Merge** are *virtual nodes* — they control flow without dispatching any job.

### Edges and Ports

Edges connect nodes and define execution order. They can carry optional **source ports** for conditional routing:

```
Step A ──────────────────► Step B         (unconditional)

Condition ──── true  ────► Step B
           └── false ────► Step C         (conditional)
```

Condition node outputs are named ports: `true` and `false`. Connect downstream steps to the appropriate port in the Workflow Builder.

### Runs

Each time a workflow is triggered (manually or by cron), a **WorkflowRun** is created. It tracks:

- Overall status (`Pending` → `Running` → `Completed / Failed / PartiallyCompleted / Cancelled`)
- Start/end time, total duration
- Individual step statuses (one `JobOccurrence` per Task step)
- Trigger reason

---

## Workflow Settings

When creating or editing a workflow, the following settings are available:

| Setting | Description |
|---------|-------------|
| **Name** | Display name (required) |
| **Description** | Optional human-readable description |
| **Tags** | Comma-separated labels for filtering |
| **Active** | Whether the workflow can be triggered |
| **Cron Expression** | 6-field cron for automatic scheduling (e.g. `0 0 9 * * *` = daily at 9:00 AM). Leave empty for manual-only. |
| **Failure Strategy** | How the engine handles step failures (see below) |
| **Max Step Retries** | Number of times a failed step is retried before marking it as failed |
| **Max Retry-After** | Number of times a step may be rerun at the time its job asked for (see [Retry at a Specific Time](#retry-at-a-specific-time)). Default 10. Not counted against Max Step Retries. |
| **Timeout (seconds)** | Maximum total duration. The run is cancelled if exceeded. `null` = no timeout. |

### Failure Strategies

| Strategy | Behavior |
|----------|----------|
| **Stop on First Failure** | The entire workflow stops when any step fails. All pending steps are skipped. |
| **Continue on Failure** | Independent parallel branches keep running. Only steps that depend on the failed step are skipped. |

---

## Step Settings

Each Task step has the following configuration:

| Setting | Description |
|---------|-------------|
| **Step Name** | Label shown in the DAG builder and run history |
| **Job** | Which scheduled job this step executes |
| **Delay (seconds)** | Wait this many seconds after dependencies complete before dispatching |
| **Job Data Override** | A static JSON object that replaces the job's default data for this run. **Disabled when Data Mappings are active.** |
| **Data Mappings** | Dynamic rules to forward output fields from parent steps into this step's job data (see [Data Mappings](#data-mappings)) |

Condition steps additionally have a **Condition Expression** field (see [Condition Nodes](#condition-nodes)).

---

## Data Mappings

Data Mappings allow you to pass output fields from a completed upstream step into the job data of a downstream step, making workflows truly data-driven.

### Format

Internally, mappings are stored as a JSON dictionary on the step:

```json
{
  "sourceStepId:sourcePath": "targetPath"
}
```

| Part | Example | Description |
|------|---------|-------------|
| `sourceStepId` | `019d13f6-e0d5-7286-be33-945cdb1c83f7` | ID of the upstream step whose result to read from |
| `sourcePath` | `complexProp.title` | Dot-separated JSON path into the upstream result |
| `targetPath` | `subject` | Key to set in this step's job data |

> Path lookup is **case-insensitive** — `complexProp.title` matches both `complexProp.title` and `ComplexProp.Title` in the upstream result.

### Schema-Assisted Selection

In the Workflow Builder, when a job is selected the **Input Schema** and **Output Schema** panels appear — generated from the job's C# types. You can pick source and target fields from the dropdowns, with nested object properties shown as dotted paths.

You can also type a **custom path** directly into the search box and press Enter.

### Wildcard Mapping

Select `* entire result` as the source path to pass the entire JSON result object of the upstream step as-is.

### Interaction with Job Data Override

| Condition | Behavior |
|-----------|----------|
| No mappings configured | **Job Data Override** is editable and used as-is |
| ≥1 mapping configured | **Job Data Override** is disabled and cleared. Mappings take full control of job data. |

### Example

```
Step 1 (ExtractPrices) → result: { "price": 99, "item": { "name": "Widget" } }
Step 2 (SendInvoice)   → job data: { "amount": null, "title": null }

Mapping on Step 2:
  step1Id:price      → amount
  step1Id:item.name  → title

Result job data for Step 2:
  { "amount": 99, "title": "Widget" }
```

---

## Condition Nodes

A Condition node evaluates an expression against the results and statuses of its parent steps, then routes execution through either the `true` or `false` port.

### Expression Syntax

```
[stepId:](@status|$.field) operator value
```

Expressions support:

- `&&` (AND — higher precedence) and `||` (OR)
- `@status` — checks a step's `WorkflowStepStatus`
- `$.field` — checks a field in a step's JSON result
- Optional `stepId:` prefix to target a specific parent step; otherwise all parents are checked

### Operators

| Operator | Applicable to |
|----------|---------------|
| `==`, `!=` | Status and string fields |
| `>`, `<`, `>=`, `<=` | Numeric fields |

### Examples

```
# All parents completed
@status == 'Completed'

# A specific parent completed
019d...83f7:@status == 'Completed'

# Result field check
$.price > 100

# Combined
@status == 'Completed' && $.price > 50

# OR branch
$.status == 'approved' || $.status == 'auto-approved'

# Mixed with step prefix
019d...83f7:$.price > 100 && @status != 'Skipped'
```

> If the expression cannot be parsed or evaluated, it defaults to `true` (the step executes).

---

## Execution Engine

The Workflow Engine runs as a **background service** inside the Milvaion API, polling at a configurable interval (`WorkflowEngine:PollingIntervalSeconds`).

### Execution Loop

```
Every polling interval:
  1. Check cron-scheduled workflows → trigger due ones
  2. Load all Pending/Running workflow runs from DB
  3. For each run:
     a. Evaluate virtual nodes (Condition, Merge) in dependency order
     b. Find Task steps whose all dependencies are satisfied
     c. Apply data mappings to build final job data
     d. Dispatch ready steps via RabbitMQ
  4. Bulk-save all state changes to DB
```

### Step Lifecycle

```
Pending → (dependencies satisfied) → Delayed (if delay > 0)
                                    → Running (dispatched to worker)
                                    → Completed | Failed | Skipped | Cancelled
Failed with retry-after → Delayed (until the requested time) → Running
```

### Run Status Transitions

| Status | Meaning |
|--------|---------|
| `Pending` | Run created, engine has not processed it yet. A run triggered for later stays here until its scheduled time. |
| `Running` | At least one step is active |
| `Completed` | All steps completed successfully |
| `PartiallyCompleted` | Some steps failed or were skipped, but at least one succeeded |
| `Failed` | All steps either failed or were never reached — no successful completions |
| `Cancelled` | Run was cancelled manually or due to a timeout |

### Zombie Detection

Task steps that are `Running` and have not received a heartbeat within their `ZombieTimeoutMinutes` threshold are detected and marked as zombie/failed, preventing runs from getting stuck indefinitely. A dispatched step that no worker has picked up is measured from its dispatch time, not from when the run was created, so long waits before dispatch (a scheduled run, slow upstream steps, a retry-after) don't count against it. `Pending` and `Delayed` steps are never treated as zombies.

### Retry at a Specific Time

Sometimes a step can't run now but you know when it can: an API quota resets at 11:00 UTC, a rate limit window ends, a partner system is down for planned maintenance. Retrying with backoff would just burn attempts. Instead the job throws a **retry-after** error carrying the time:

```csharp
// .NET
throw new RetryAfterException(DateTime.UtcNow.Date.AddHours(11), "YouTube upload quota used up");
```

```ts
// Node.js
import { RetryAfterError } from '@milvasoft/milvaion-worker';

throw new RetryAfterError(new Date('2026-11-15T11:00:00Z'), 'YouTube upload quota used up');
```

Every SDK has it (Python `RetryAfterError`, Go `milvaion.RetryAfter(...)`, Java and PHP `RetryAfterException`, Rust `JobError::retry_after(...)`, Ruby `Milvaion::RetryAfterError`); see [Workers in Other Languages](26-other-language-workers.md#how-a-job-ends).

What happens:

1. The worker doesn't retry the job itself and doesn't send it to the dead letter queue. It reports the occurrence as **Failed** with the requested time.
2. The engine puts the step in **Delayed** until that time, then dispatches it again. A time that has already passed dispatches it right away. Other steps and the run carry on as usual; the run stays `Running`.
3. These reruns are counted apart from **Max Step Retries**, and they don't count towards the job's auto-disable threshold or raise a failure alert. **Max Retry-After** (default 10) caps them per step and run; once it is used up, the step's failure is handled like any other failure.

The workflow's **Timeout** still applies while a step waits, so leave room for the longest wait you expect.

In a standalone job (not a workflow step) a retry-after error ends the occurrence as Failed with no retry.

---

## Triggering Workflows

### Manual Trigger

Workflows can be triggered from the Milvaion Portal (the **Trigger** button on the Workflows list or detail page) or via the API:

```http
POST /api/v1/workflows/workflow/trigger
Content-Type: application/json

{
  "workflowId": "0195f0c2-...",
  "reason": "Publish post 42",
  "stepJobData": { "<stepId>": "{\"postId\": 42}" }
}
```

`stepJobData` is optional: per-step job data for this run, merged on top of the job's and the step's own data.

### Scheduled Run (Once, at a Given Time)

Add `executeAt` (UTC) to start the run once at that time instead of right away. This is what you want for "publish this post at 18:00", with no extra one-time job in between:

```http
POST /api/v1/workflows/workflow/trigger

{
  "workflowId": "0195f0c2-...",
  "reason": "Publish post 42",
  "executeAt": "2026-11-15T18:00:00Z",
  "stepJobData": { "<stepId>": "{\"postId\": 42}" }
}
```

The response carries the run id and `scheduledAt`. Until its time the run stays `Pending`; the run list and run page show when it will start. Before it starts you can:

- **Move it:**
  ```http
  PUT /api/v1/workflows/workflow/run/schedule

  { "workflowRunId": "0195f0c3-...", "executeAt": "2026-11-15T20:00:00Z" }
  ```
  A run that has already started (or was never scheduled) can't be moved. A time in the past starts it right away.
- **Cancel it:** `POST /api/v1/workflows/workflow/cancel` with `{ "workflowRunId": "..." }`, the same as cancelling a running run. Its steps are marked Skipped.

`executeAt` more than 5 minutes in the past is rejected. Over MCP, `trigger_workflow` takes `executeAt` and `stepJobData`, and `reschedule_workflow_run` moves a scheduled run.

### Cron Schedule

Set a **Cron Expression** on the workflow. The engine automatically triggers the workflow when the cron schedule fires:

```
0 0 9 * * *       → Every day at 09:00 UTC
0 */30 * * * *    → Every 30 minutes
0 0 0 * * MON     → Every Monday at midnight
```

> Uses the 6-field cron format (seconds included). See [Core Concepts — Cron Expressions](03-core-concepts.md#cron-expressions) for full reference.

---

## Building a Workflow (Step-by-Step)

### 1. Open the Builder

Navigate to **Workflows** → click **New Workflow** or open an existing one and click **Edit / Builder**.

### 2. Configure Workflow Settings

Fill in the name, optional cron expression, and choose a failure strategy in **Settings**.

### 3. Add Nodes

Click **Add Node ▼** in the toolbar to add:

- **Task Step** — select the job to run
- **Condition** — write a branching expression
- **Merge** — join parallel branches

### 4. Connect Nodes

Drag from the bottom handle of one node to the top handle of another to create an edge. For Condition nodes, drag from the `true` (right) or `false` (bottom) port.

### 5. Configure Each Step

Click a node to open the **Step Config Panel**:

1. Set a **Step Name**
2. For Task nodes: select the **Job**
3. Optionally set a **Delay**
4. Add **Data Mappings** to forward upstream results

### 6. Save

Click **Save** in the toolbar. The workflow version is incremented and the definition is persisted.

---

## Versioning

Every save creates a new version. Older versions are stored as snapshots in `Workflow.Versions`. This allows you to:

- See when the DAG definition changed
- Compare what a historical run was executing against

Active runs always execute against the workflow version that was current when the run was triggered.

---

## Limitations

| Limitation | Detail |
|------------|--------|
| **No cycles** | The DAG must be acyclic. Circular dependencies are not detected at save time but will cause runs to stall. |
| **Delay on virtual nodes** | Delay is only supported on Task nodes. Condition and Merge nodes execute instantly when their dependencies are satisfied. |
| **Parallel scaling** | All runs for a workflow execute concurrently — there is no built-in workflow-level concurrency limit. |
| **Result size** | Step results are stored in PostgreSQL as plain JSON strings. Very large results (> a few MB) may impact performance. |

---

## Related Topics

- [Implementing Jobs](05-implementing-jobs.md) — Write jobs that return typed results for use in Data Mappings
- [Core Concepts](03-core-concepts.md) — Job, Occurrence, Worker fundamentals
- [Reliability](08-reliability.md) — Retry, timeout, and zombie detection details
- [Configuration](06-configuration.md) — `WorkflowEngine` section for polling interval and enable/disable
