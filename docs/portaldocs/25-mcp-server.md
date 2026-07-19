---
id: mcp-server
title: "MCP Server"
sidebar_label: "MCP Server"
sidebar_position: 122
description: "Connect Claude Code, Cursor or GitHub Copilot to Milvaion over the Model Context Protocol and ask about jobs, failures and workers in plain language."
keywords: [milvaion mcp, model context protocol, claude code, cursor, copilot, ai job scheduler]
---

# MCP Server

Milvaion exposes a [Model Context Protocol](https://modelcontextprotocol.io/) server at `/mcp`. Point an AI coding assistant at it and you can ask things like *"which jobs failed last night and why?"* instead of clicking through the dashboard.

## Which Way Round Does This Work?

This is the part people usually get backwards, so it is worth being explicit.

```text
Your machine                          Your Milvaion server
┌────────────────────────┐           ┌──────────────────┐
│ Claude Code / Cursor / │           │  Milvaion API    │
│ Copilot                │ ──MCP──▶  │  /mcp            │
│                        │           │                  │
│ • the model runs here  │ ◀──JSON── │ • jobs           │
│ • your subscription    │           │ • logs           │
│ • your tokens          │           │ • metrics        │
└────────────────────────┘           └──────────────────┘
```

**Milvaion never calls a language model.** It does not store an OpenAI, Anthropic or Google key, and it does not consume tokens. It is the data source; the model lives entirely in your editor, on your subscription.

The only credential involved is a **Milvaion api key**, which your editor uses to authenticate to Milvaion.

## Setup

### 1. Create a read-only api key

Follow [Api Keys](./24-api-keys.md) and grant only:

- `ScheduledJobManagement.List`, `ScheduledJobManagement.Detail`
- `FailedOccurrenceManagement.List`
- `WorkerManagement.List`
- `WorkflowManagement.List`

With this set the assistant can investigate anything and change nothing. Add `ScheduledJobManagement.Trigger` or `WorkflowManagement.Trigger` later if you want it to be able to run jobs.

### 2. Register the server in your client

Milvaion is a **remote HTTP** MCP server, so any client that supports streamable HTTP transport can connect. What differs between clients is the config file location and, annoyingly, the key names.

| Client | Config location | Top-level key | URL field |
|--------|-----------------|---------------|-----------|
| Claude Code | `.mcp.json` or `claude mcp add` | `mcpServers` | `url` |
| Cursor | `.cursor/mcp.json` | `mcpServers` | `url` |
| VS Code / Copilot | `.vscode/mcp.json` | **`servers`** | `url` |
| Windsurf | `~/.codeium/windsurf/mcp_config.json` | `mcpServers` | **`serverUrl`** |
| Gemini CLI | `~/.gemini/settings.json` | `mcpServers` | `httpUrl` |
| Claude Desktop | Connectors UI | — | — |
| ChatGPT | Connectors UI | — | — |

Getting `servers` and `mcpServers` the wrong way round, or `url` instead of `serverUrl`, is the single most common reason a client silently lists no tools.

---

#### Claude Code

No file editing needed:

```bash
claude mcp add --transport http milvaion \
  https://milvaion.yourcompany.com/mcp \
  --header "X-ApiKey: $MILVAION_API_KEY"
```

Add `--scope project` to write it to `.mcp.json` and share it with the team. Verify with `claude mcp list`, or type `/mcp` inside a session to see the tool list.

#### Cursor

`.cursor/mcp.json` in the project, or the global equivalent from **Settings → MCP**:

```json
{
  "mcpServers": {
    "milvaion": {
      "type": "http",
      "url": "https://milvaion.yourcompany.com/mcp",
      "headers": {
        "X-ApiKey": "your-api-key"
      }
    }
  }
}
```

#### VS Code / GitHub Copilot

`.vscode/mcp.json`. Note the top-level key is `servers`, and `type` is required:

```json
{
  "inputs": [
    {
      "id": "milvaion-key",
      "type": "promptString",
      "description": "Milvaion api key",
      "password": true
    }
  ],
  "servers": {
    "milvaion": {
      "type": "http",
      "url": "https://milvaion.yourcompany.com/mcp",
      "headers": {
        "X-ApiKey": "${input:milvaion-key}"
      }
    }
  }
}
```

The `inputs` block makes VS Code prompt for the key at runtime instead of storing it in the file.

:::warning Agent mode is required

MCP tools do not appear in Copilot's default Ask mode. Switch the chat to **Agent** mode or the server will look connected while nothing can call it.

:::

#### Windsurf

`~/.codeium/windsurf/mcp_config.json`. Windsurf uses `serverUrl`, not `url`:

```json
{
  "mcpServers": {
    "milvaion": {
      "serverUrl": "https://milvaion.yourcompany.com/mcp",
      "headers": {
        "X-ApiKey": "your-api-key"
      }
    }
  }
}
```

#### Gemini CLI

`~/.gemini/settings.json` for all projects, or `.gemini/settings.json` for one. Gemini CLI expands `${VAR}` from your environment, so the key never has to sit in the file:

```json
{
  "mcpServers": {
    "milvaion": {
      "httpUrl": "https://milvaion.yourcompany.com/mcp",
      "headers": {
        "X-ApiKey": "${MILVAION_API_KEY}"
      }
    }
  }
}
```

Restart the CLI after editing. `includeTools` and `excludeTools` let you narrow the surface further on the client side — though scoping the api key is the stronger control, since it is enforced server side.

#### Claude Desktop

Claude Desktop's `claude_desktop_config.json` validates **stdio servers only**. Putting a `url` in it does nothing — the entry is ignored, which is why the tools never show up.

Two options:

1. **Connectors UI** — Settings → Connectors → Add custom connector, and paste the `/mcp` URL. This path requires **HTTPS**, so a `localhost` instance will not work.
2. **`mcp-remote` bridge** — run a local stdio process that forwards to your HTTP endpoint:

```json
{
  "mcpServers": {
    "milvaion": {
      "command": "npx",
      "args": [
        "-y", "mcp-remote",
        "http://localhost:5000/mcp",
        "--header", "X-ApiKey:${MILVAION_API_KEY}"
      ],
      "env": { "MILVAION_API_KEY": "your-api-key" }
    }
  }
}
```

For local development, Claude Code is far less friction than either of these.

#### ChatGPT

Settings → Connectors → **Advanced → Developer mode**, then add a connector pointing at your `/mcp` URL.

:::warning ChatGPT limitations worth knowing before you try

- **HTTPS only, remote only.** No stdio, and no `localhost` — expose the endpoint through a tunnel or a real domain first.
- **Write tools need a Business, Enterprise or Edu workspace.** On individual Plus and Pro plans, custom connectors are restricted to read and fetch operations, so `trigger_job` and anything else that changes state will not be callable.
- **The connector must be enabled per conversation.** Turning it on once in settings is not enough; most "it stopped working" reports are this.

:::

For a read-only Milvaion key the Plus/Pro restriction does not matter much, since the fifteen reading tools are the ones you would grant anyway.

#### Google AI Studio

AI Studio does not currently expose custom MCP connectors in the same way. For a Google-based workflow, use **Gemini CLI** as documented above.

---

:::tip Keep the key out of the repository

Every client above supports either environment variable expansion or a runtime prompt. Use it. A config file with a live key in it will eventually be committed, and a key in git history means creating a new one and revoking the old.

:::

### 3. Ask something

> Which jobs failed last night?

> Job `daily-invoice-export` has been failing since Tuesday. Read the logs and tell me what changed.

> Is there a worker alive that can run `SendReportJob`?

## Available Tools

Every tool is gated by the permission in the right hand column. A key that lacks a permission cannot call the tool, so the tool surface an assistant actually sees is decided entirely by how you scope the key.

### Reading

| Tool | What it does | Permission |
|------|--------------|------------|
| `get_overview` | Dashboard statistics: counts, throughput, worker health | `ScheduledJobManagement.List` |
| `list_jobs` | Scheduled jobs, with free text search | `ScheduledJobManagement.List` |
| `get_job` | One job in full: schedule, worker, job data, retry and timeout settings | `ScheduledJobManagement.Detail` |
| `list_tags` | Distinct tags in use across jobs | `ScheduledJobManagement.List` |
| `list_occurrences` | Executions with status, duration and result | `ScheduledJobManagement.List` |
| `get_occurrence` | One execution with its logs and exception detail | `ScheduledJobManagement.Detail` |
| `list_failures` | Jobs that exhausted their retries and reached the dead letter queue | `FailedOccurrenceManagement.List` |
| `get_failure` | One dead letter record with exception and resolution notes | `FailedOccurrenceManagement.Detail` |
| `list_workers` | Workers with heartbeat status and executable job types | `WorkerManagement.List` |
| `get_worker` | One worker in full | `WorkerManagement.Detail` |
| `list_workflows` | Workflows | `WorkflowManagement.List` |
| `get_workflow` | One workflow with its step graph | `WorkflowManagement.Detail` |
| `list_workflow_runs` | Workflow runs and their status | `WorkflowManagement.List` |
| `get_workflow_run` | One run, steps in order with dependencies by name | `WorkflowManagement.Detail` |
| `list_activity_logs` | Who changed what, and when | `ActivityLogManagement.List` |
| `summarize_logs` | Execution log counts by level, category, exception, job and message | `ScheduledJobManagement.Detail` |
| `search_logs` | Individual execution log lines | `ScheduledJobManagement.Detail` |
| `list_reports` | Metric report metadata and freshness, without payloads | `ScheduledJobManagement.List` |
| `get_latest_report` | Newest report of a metric type, with its data | `ScheduledJobManagement.Detail` |
| `get_report` | One report by id, for comparing against an older one | `ScheduledJobManagement.Detail` |
| `get_system_health` | Health of the scheduler, database, Redis and RabbitMQ | `SystemAdministration.List` |
| `get_queue_stats` | RabbitMQ queue depths and consumer counts | `SystemAdministration.List` |
| `get_queue_info` | Depth detail for one queue | `SystemAdministration.List` |
| `get_job_statistics` | Aggregate counters across jobs and executions | `SystemAdministration.List` |
| `get_database_statistics` | Size, index efficiency, cache hit ratio, bloat | `SystemAdministration.List` |
| `get_configuration` | Effective scheduler configuration | `SystemAdministration.List` |
| `list_notifications` | Alerts Milvaion itself raised | `InternalNotificationManagement.List` |
| `list_permissions` | The permission catalog, for explaining what to grant | `PermissionManagement.List` |

### Running and pausing

| Tool | What it does | Permission |
|------|--------------|------------|
| `trigger_job` | Runs a job immediately, outside its schedule | `ScheduledJobManagement.Trigger` |
| `cancel_occurrence` | Cancels a running execution | `ScheduledJobManagement.Trigger` |
| `set_job_active` | Pauses or resumes a job without deleting it | `ScheduledJobManagement.Update` |
| `trigger_workflow` | Starts a workflow run immediately | `WorkflowManagement.Trigger` |
| `cancel_workflow_run` | Cancels a run in progress | `WorkflowManagement.Trigger` |

### Editing

| Tool | What it does | Permission |
|------|--------------|------------|
| `create_job` | Creates a scheduled job | `ScheduledJobManagement.Create` |
| `update_job` | Changes name, description, tags, cron, job data or timeout | `ScheduledJobManagement.Update` |
| `resolve_failures` | Marks dead letter records resolved with a note | `FailedOccurrenceManagement.Update` |

### Deleting

| Tool | What it does | Permission |
|------|--------------|------------|
| `delete_job` | Deletes a job and its history | `ScheduledJobManagement.Delete` |
| `delete_occurrences` | Deletes execution records | `ScheduledJobManagement.Delete` |
| `delete_failures` | Deletes dead letter records | `FailedOccurrenceManagement.Delete` |
| `delete_worker` | Removes a worker registration | `WorkerManagement.Delete` |
| `delete_workflow` | Deletes a workflow and its run history | `WorkflowManagement.Delete` |

### Filtering

The list tools filter rather than make the assistant page through everything:

| Tool | Filters |
|------|---------|
| `list_jobs` | `tag`, `workerId`, `isActive`, free text |
| `list_occurrences` | `jobId`, `status`, `workerId`, `since`, `until`, free text |
| `list_failures` | `jobId`, `resolved`, `since`, `until`, free text |
| `list_activity_logs` | `since`, `until` |
| `summarize_logs` | `jobId`, `level`, `since`, `until`, free text |
| `search_logs` | `jobId`, `occurrenceId`, `level`, `category`, `exceptionType`, `since`, `until`, free text |
| `list_workflow_runs` | `workflowId` |

All times are UTC. `since` on its own is the common case — "what failed since midnight" needs one bound, not two.

`get_occurrence` returns the tail of the log rather than all of it, defaulting to the last 100 lines with a `logLines` parameter to raise it. A job that logs in a loop can otherwise fill an assistant's entire context in one call.

### Prompts

Three prompt templates ship with the server, selectable in clients that support them:

| Prompt | What it does |
|--------|--------------|
| `diagnose_job` | Walks a failing job in order: failures, pattern, logs, worker health, then recent config changes |
| `overnight_review` | Reviews a recent window and groups failures by cause rather than listing them |
| `explain_workflow` | Describes a workflow's steps, branching and data flow in plain language |

These matter more than they look. Left to itself a model tends to start with whichever tool it read first; the prompts put a working order in front of it.

### Not exposed

Users, roles, permissions, api keys, dispatcher control, configuration and content management have no tools and are not reachable over MCP, regardless of what the key is granted. Workflow creation and editing are also absent: authoring a directed graph with conditions and data mappings belongs in the visual builder, not in a tool call.

:::tip Scope the key, not the tool list

You do not choose which tools to expose — you choose what the key can do. Granting only `List` and `Detail` permissions leaves an assistant with the fifteen reading tools and nothing else, which is the right default for most people.

:::

## Security Model

**Authentication happens at the endpoint.** `/mcp` requires a valid credential; an anonymous request never reaches a tool.

**Authorization happens per tool.** Each tool checks its own permission before doing any work. A key without `ScheduledJobManagement.Trigger` calling `trigger_job` gets an error naming the missing permission — the assistant is told exactly what it lacks, so it stops retrying and can tell you what to grant.

**Side effects are attributed.** Anything triggered, cancelled or resolved through MCP is recorded with the key's name, so the history distinguishes it from a human acting in the dashboard.

**Concurrency policies are never bypassed.** `trigger_job` always dispatches with force disabled. Overriding a job's concurrency policy stays a deliberate human action in the dashboard.

**Fields cannot be cleared by accident.** `update_job` only changes the arguments it is given; an omitted argument is left alone rather than blanked. The trade-off is that clearing a field on purpose has to be done in the dashboard, which is the safer way round.

**Tools declare what they do.** Every tool carries the MCP annotations clients use to decide whether to prompt: reading tools are marked read-only, `delete_job` and the other four deletions are marked destructive, and the `set_*` and `update_*` tools are marked idempotent. A client that offers "always allow" for a tool can then treat `list_jobs` and `delete_job` differently, which without these it cannot.

:::warning Treat write permissions as production access

`Trigger` lets an assistant cause real work to run. `Update` lets it change when your jobs run. `Delete` is irreversible. The tool descriptions tell the model to confirm before doing any of this, and to prefer `set_job_active` over `delete_job` — but those are prompts, not guarantees.

Grant `Create`, `Update` and `Delete` only where you would be comfortable with the same person having dashboard access. For everything else, a read-only key is the right answer.

:::

## Verifying It Works

`/mcp` speaks JSON-RPC over HTTP, so you can exercise it with `curl` before involving a client.

List the tools:

```bash
curl -X POST http://localhost:5000/mcp \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -H "X-ApiKey: $MILVAION_API_KEY" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

Call one:

```bash
curl -X POST http://localhost:5000/mcp \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -H "X-ApiKey: $MILVAION_API_KEY" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_overview","arguments":{}}}'
```

Without the header the same request returns 401, which is the quickest confirmation that the endpoint is protected.

## Troubleshooting

| Symptom | Cause |
|---------|-------|
| **401 Unauthorized** | Missing or malformed `X-ApiKey` header, or the key was revoked or expired. |
| **Api key was issued with a retired signing secret** | `ApiKey.Version` was incremented after the key was created. Create a new key. |
| **"does not have the '...' permission"** | Working as intended. Grant that permission to the key, or use a different key. |
| **404 on /mcp** | The API predates MCP support, or a reverse proxy is not forwarding the path. |
| **Client connects but lists no tools** | Usually a proxy stripping the `Accept: text/event-stream` header. |

## Deployment Notes

The server runs in **stateless** mode, so any API replica can serve any request and no sticky sessions are needed behind a load balancer. Server-initiated features that depend on a persistent session — sampling, elicitation, roots — are therefore not available.

If Milvaion is behind a reverse proxy, make sure `/mcp` is forwarded and that `Content-Type` and `Accept` headers survive the hop.

## Next Steps

- **[Api Keys](./24-api-keys.md)** — creating and scoping the credential this needs
- **[Monitoring](./10-monitoring.md)** — the metrics behind `get_overview`
- **[Workflows](./20-workflows.md)** — what `list_workflows` is showing you
