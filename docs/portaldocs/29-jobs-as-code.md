---
id: jobs-as-code
title: "Jobs as Code"
sidebar_label: "Jobs as Code"
sidebar_position: 124
description: "Keep Milvaion jobs in version control and apply them from CI with the milvaion CLI or the Terraform provider."
keywords: [milvaion cli, milvaion terraform, jobs as code, gitops scheduler, terraform job scheduler, yaml jobs]
---

# Jobs as Code

Clicking jobs together in the dashboard is fine for one environment. Once you have staging and production, or several teams, you want job definitions in Git: reviewed in pull requests, applied by CI, the same in every environment.

Milvaion offers two ways to do that. Both use the regular REST API with an [api key](./24-api-keys.md), so they need nothing extra on the server.

| | **milvaion CLI** | **Terraform provider** |
|---|---|---|
| Definitions | YAML files (`apiVersion: milvaion/v1`) | HCL, `milvaion_job` resources |
| State | None. Jobs are matched by name on every run | Terraform state |
| Good for | Teams without Terraform, quick scripts, CI checks | Teams that already manage infrastructure with Terraform |
| Also does | `get`, `trigger`, `logs`, `export` | Data sources for jobs and workers |

Pick one tool per job. If the CLI and Terraform both manage the same job, each one will keep undoing the other's changes.

## The Api Key

Create a key under **User Management → Api Keys** with these permissions:

| Use | Permissions |
|-----|-------------|
| `apply`, `diff`, Terraform | `ScheduledJobManagement.List`, `.Detail`, `.Create`, `.Update`, `.Delete` |
| `get workers`, `milvaion_worker` | `WorkerManagement.List`, `WorkerManagement.Detail` |
| `trigger` | `ScheduledJobManagement.Trigger` |
| `get occurrences`, `logs` | `ScheduledJobManagement.List`, `.Detail` |

A drift check in CI (`milvaion diff --exit-code`) only needs `List` and `Detail`.

## The milvaion CLI

### Install

Download the archive for your platform from the [GitHub releases](https://github.com/Milvasoft/milvaion/releases) tagged `cli-v*`, extract `milvaion` and put it on your `PATH`. Each release has a checksums file to verify the download.

With Go installed you can also build it from source:

```bash
go install github.com/Milvasoft/milvaion/tools/milvaion-cli@latest
```

That installs the binary as `milvaion-cli`; rename it to `milvaion` if you like.

### Log In

```bash
milvaion login --url https://milvaion.example.com --api-key "$KEY"
```

`login` checks the key and saves the URL and key to `milvaion/config.yaml` in your user config directory (`~/.config` on Linux, `~/Library/Application Support` on macOS, `%AppData%` on Windows), readable only by you. Set `MILVAION_CONFIG` to use another file. To keep the key out of your shell history, pipe it in with `--api-key-stdin`.

In CI, skip `login` and set environment variables instead. They override the saved login:

```bash
export MILVAION_URL=https://milvaion.example.com
export MILVAION_API_KEY=...
```

### Describe Jobs in YAML

```yaml
apiVersion: milvaion/v1
kind: Job
metadata:
  name: nightly-invoice-report      # the job's display name, and the key apply matches on
spec:
  description: Sends yesterday's invoices to finance
  tags: [billing, reports]
  worker: reporter-worker            # worker id
  jobName: InvoiceReportJob          # job type on that worker
  cron: "0 0 2 * * *"                # six fields, seconds first: 02:00 every day
  concurrencyPolicy: Skip            # Skip or Queue
  jobData:                           # any YAML, sent to the job as JSON
    recipients: [finance@example.com]
    format: pdf
  executionTimeoutSeconds: 900
  autoDisable:
    enabled: true
    threshold: 5
    failureWindowMinutes: 60
```

Put several jobs in one file, either as YAML documents separated by `---`, or as a list:

```yaml
apiVersion: milvaion/v1
kind: JobList
items:
  - metadata: {name: cleanup-temp-files}
    spec:
      worker: maintenance-worker
      jobName: CleanupJob
      cron: "0 30 3 * * *"
  - metadata: {name: one-off-migration}
    spec:
      worker: sample-worker
      jobName: MigrateJob
      executeAt: 2026-12-01T22:00:00Z   # one-time job
      active: false
```

Run `milvaion schema` for the full field list. The fields are:

| Field | Notes |
|-------|-------|
| `description` | Free text. |
| `tags` | List of tags. |
| `worker` | Worker id. Changing it replaces the job (see below). |
| `jobName` | Job type on the worker. |
| `cron` | Recurring schedule. Six fields, seconds first. |
| `executeAt` | One-time run, RFC 3339. Ignored when `cron` is set. Changing it replaces the job. |
| `active` | Default `true`. |
| `concurrencyPolicy` | `Skip` (default) or `Queue`. |
| `jobData` | Payload. A YAML value or a JSON string. |
| `zombieTimeoutMinutes` | Leave out to use the server default. |
| `executionTimeoutSeconds` | Leave out to use the worker default. |
| `autoDisable` | `{enabled, threshold, failureWindowMinutes}`. Leave it out and apply leaves the server's settings alone. |
| `external`, `externalJobId` | For jobs reported by [Hangfire or Quartz](./18-external-scheduler.md). Changing them replaces the job. |

Leaving out any other field means "the default": removing `description` from the file clears it on the server.

### Preview and Apply

```bash
milvaion diff -f jobs/
```

```text
~ update nightly-invoice-report (01a1d6f5-59e0-7465-9423-a145e95a45a1)
      cron: "0 0 2 * * *" -> "0 0 3 * * *"
+ create cleanup-temp-files
      worker: "maintenance-worker"
      jobName: "CleanupJob"
      cron: "0 30 3 * * *"
      active: true
      concurrencyPolicy: "Skip"

Plan: 1 to create, 1 to update, 0 to replace, 0 to delete, 4 unchanged.
```

```bash
milvaion apply -f jobs/
```

`-f` takes a file, a directory of `.yaml`/`.yml` files, or `-` for standard input, and can be repeated. Running `apply` twice with the same files changes nothing the second time.

**How jobs are matched.** `metadata.name` is the job's display name, and `apply` looks the job up by it. A name that does not exist yet is created. If two jobs on the server have the same name, `apply` stops and asks you to fix that first, rather than guess.

**Renaming** a job in the file creates a new job under the new name. The old one stays (and keeps its run history) until you run `apply --prune`. To keep the history on the renamed job, rename it in the dashboard first, then in the file.

**Replacements.** The API cannot change `worker`, `executeAt`, `external` or `externalJobId` on an existing job. When one of them changes, `apply` creates the new job and then deletes the old one, which drops its run history. It refuses to do that unless you pass `--allow-replace`. If the create fails, the old job is left untouched.

### Pruning

Every job that `apply` creates or updates gets the tag `managed-by:milvaion-cli`. With `--prune`, `apply` deletes jobs that carry that tag but are no longer in the files:

```bash
milvaion apply -f jobs/ --prune
```

Jobs made in the dashboard do not have the tag, so `--prune` never touches them. When several repositories manage jobs on the same Milvaion, give each its own tag so one repository's prune cannot delete another's jobs:

```bash
milvaion apply -f jobs/ --prune --managed-tag managed-by:billing-repo
```

The tag shows up in the dashboard like any other tag. `export` and `diff` hide it.

### Adopting Existing Jobs

```bash
milvaion export > jobs.yaml
milvaion apply -f jobs.yaml
```

`export` writes the current jobs as `milvaion/v1` YAML, leaving out defaults. The first `apply` of that file only adds the managed tag. From then on, edit the file instead of the dashboard.

`export` takes `--name` (repeatable), `--tag`, `--managed`, and `-o file.yaml`. Jobs reported by external schedulers are left out unless you pass `--include-external`.

### Drift Detection in CI

`diff --exit-code` exits `0` when the server matches the files and `2` when it does not:

```yaml
# .github/workflows/milvaion-jobs.yml
name: Milvaion jobs
on:
  pull_request:
    paths: ['jobs/**']
  push:
    branches: [main]
    paths: ['jobs/**']

jobs:
  jobs:
    runs-on: ubuntu-latest
    env:
      MILVAION_URL: ${{ vars.MILVAION_URL }}
      MILVAION_API_KEY: ${{ secrets.MILVAION_API_KEY }}
    steps:
      - uses: actions/checkout@v4
      - name: Install milvaion
        run: |
          curl -sSL https://github.com/Milvasoft/milvaion/releases/download/cli-v1.0.0/milvaion_1.0.0_linux_amd64.tar.gz | tar xz milvaion
          sudo mv milvaion /usr/local/bin/
      - name: Show changes
        run: milvaion diff -f jobs/
      - name: Apply
        if: github.event_name == 'push'
        run: milvaion apply -f jobs/ --prune
```

### Everyday Commands

```bash
milvaion get jobs                     # --tag, --search, -o json|yaml
milvaion get workers
milvaion get occurrences --job nightly-invoice-report --status Failed --limit 10

milvaion trigger nightly-invoice-report                 # prints the occurrence id
milvaion trigger nightly-invoice-report --follow        # streams logs; exits 1 if the run fails
milvaion trigger nightly-invoice-report --data '{"format":"csv"}' --reason "rerun for audit"

milvaion logs 01a1d6f5-857c-7b21-995d-93ea2aea8ab5
milvaion logs --job nightly-invoice-report -f           # newest run, follow until it finishes
```

Wherever a command takes a job, you can pass its name or its id.

## The Terraform Provider

### Configure the Provider

```hcl
terraform {
  required_providers {
    milvaion = {
      source  = "milvasoft/milvaion"
      version = "~> 1.0"
    }
  }
}

provider "milvaion" {
  url = "https://milvaion.example.com"
  # api_key = var.milvaion_api_key   # or set MILVAION_API_KEY
}
```

`url` and `api_key` fall back to `MILVAION_URL` and `MILVAION_API_KEY`. Keep the key out of `.tf` files.

### Define Jobs

```hcl
data "milvaion_worker" "reporter" {
  worker_id = "reporter-worker"
}

resource "milvaion_job" "invoice_report" {
  name            = "nightly-invoice-report"
  description     = "Sends yesterday's invoices to finance"
  tags            = ["billing", "reports"]
  worker_id       = data.milvaion_worker.reporter.worker_id
  job_name        = "InvoiceReportJob"
  cron_expression = "0 0 2 * * *"

  job_data = jsonencode({
    recipients = ["finance@example.com"]
    format     = "pdf"
  })

  execution_timeout_seconds = 900

  auto_disable = {
    threshold              = 5
    failure_window_minutes = 60
  }

  lifecycle {
    # Fail the plan early if the worker does not offer this job type.
    precondition {
      condition     = contains(data.milvaion_worker.reporter.job_names, "InvoiceReportJob")
      error_message = "reporter-worker does not offer InvoiceReportJob."
    }
  }
}
```

| Attribute | Notes |
|-----------|-------|
| `name` | Required. Display name. |
| `description`, `tags`, `job_name`, `cron_expression` | Updated in place. |
| `worker_id`, `execute_at`, `is_external`, `external_job_id` | Changing them replaces the job. |
| `is_active` | Default `true`. |
| `concurrent_execution_policy` | `Skip` (default) or `Queue`. |
| `job_data` | JSON string. Use `jsonencode()`. Key order and whitespace do not cause diffs. |
| `zombie_timeout_minutes`, `execution_timeout_seconds` | Leave out to use the defaults. |
| `auto_disable` | Leave it out to keep whatever the server has. Removing it later stops managing it; it does not reset it. |

Terraform tracks jobs by id, not by name, so renaming a `milvaion_job` updates it in place and keeps its history.

### Import Existing Jobs

```bash
terraform import milvaion_job.invoice_report 01a1d6f5-59e0-7465-9423-a145e95a45a1
```

Or, with Terraform 1.5 and later:

```hcl
import {
  to = milvaion_job.invoice_report
  id = "01a1d6f5-59e0-7465-9423-a145e95a45a1"
}
```

`milvaion get jobs` lists the ids.

### Data Sources

```hcl
# Look a job up by name (it must be unique) or by id.
data "milvaion_job" "cleanup" {
  name = "cleanup-temp-files"
}

# A registered worker: status, version, job_names, routing_patterns, instance_count.
data "milvaion_worker" "maintenance" {
  worker_id = "maintenance-worker"
}

output "cleanup_schedule" {
  value = data.milvaion_job.cleanup.cron_expression
}
```

## Things to Know

- **Cron has six fields.** Milvaion cron expressions start with seconds: `0 */5 * * * *` is every five minutes. Both tools reject five-field expressions before calling the API.
- **Workers must be running.** Milvaion checks that the worker exists, is active and offers the job type when a job is created. Start the worker before you apply.
- **Times are UTC.** `executeAt` / `execute_at` accept any offset and store UTC.
- **Edits in the dashboard are drift.** The next `apply` puts the file's values back. Pausing a job in the dashboard during an incident is fine, but expect the next deploy to switch it on again unless the file says `active: false`.

## Next Steps

- **[Api Keys](./24-api-keys.md)**: create and rotate the key the tools use
- **[MCP Server](./25-mcp-server.md)**: ask an AI assistant about the jobs you deployed
- **[Workflows](./20-workflows.md)**: chain jobs into pipelines
