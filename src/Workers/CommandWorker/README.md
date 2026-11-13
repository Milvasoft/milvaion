# Command Worker

Built-in Milvaion worker for running scripts and programs. Full documentation: [Command Worker](../../../docs/portaldocs/27-command-worker.md).

The Command Worker runs scripts and programs as Milvaion jobs: a Python script, a PHP artisan command, a Bash backup, a Go binary, anything the worker's machine or container can execute. You get everything Milvaion gives other jobs without writing an SDK integration:

- scheduling
- live logs
- retries
- the dead letter queue
- cancellation
- timeouts
- alerts

### How it works

- The commands a job may run are **defined in the worker's configuration**, by name. Job data picks a command and fills in its declared parameters. It never supplies the program or a shell line.
- Every **output line** goes to the occurrence log as it is written. Standard error lines are logged as warnings.
- The **exit code** decides the outcome:
  - A success code (default `0`) completes the job.
  - A code listed as a permanent failure sends the job to the dead letter queue without retrying.
  - Any other code is retried with backoff.
- **Cancelling** a job from the dashboard, or a **timeout**, kills the whole process tree.
- The **result** contains the exit code and the end of standard output.

### Configuration

```json
{
  "CommandWorker": {
    "AllowAdHocCommands": false,
    "MaxLoggedLines": 1000,
    "MaxResultOutputChars": 8192,
    "InheritEnvironment": true,
    "Commands": {
      "backup-database": {
        "Description": "Dumps a database to object storage",
        "FileName": "/scripts/backup.sh",
        "Arguments": ["--database", "{database}", "--target", "{target}"],
        "WorkingDirectory": "/scripts",
        "Environment": { "BACKUP_MODE": "full" },
        "Parameters": {
          "database": { "Required": true, "Pattern": "[a-z0-9_]+" },
          "target": { "Default": "s3", "Pattern": "s3|gcs" }
        },
        "PermanentFailureExitCodes": [2]
      },
      "send-report": {
        "FileName": "/usr/bin/python3",
        "Arguments": ["/scripts/report.py", "--month", "{month}"],
        "Parameters": { "month": { "Required": true, "Pattern": "\\d{4}-\\d{2}" } }
      }
    }
  }
}
```

| Command setting | Description |
|---|---|
| `FileName` | Program to start. It is started directly, not through a shell. |
| `Arguments` | One entry per argument. `{name}` inserts a parameter value; `{{` and `}}` are literal braces. |
| `WorkingDirectory` | Default: the worker's. |
| `Environment` | Extra environment variables. Values may use `{name}` placeholders. |
| `Parameters` | Parameters job data may set. Each one has `Required`, `Default`, `Pattern` (a regular expression the whole value must match) and `Description`. |
| `SuccessExitCodes` | Default: `[0]`. |
| `PermanentFailureExitCodes` | Exit codes that should not be retried. |

| Worker setting | Default | Description |
|---|---|---|
| `AllowAdHocCommands` | `false` | Lets job data name any program and arguments. Anyone who can create a job can then run anything the worker's user can. |
| `MaxLoggedLines` | 1000 | Output lines logged per job; the rest are counted. |
| `MaxResultOutputChars` | 8192 | Characters of standard output kept in the result. |
| `InheritEnvironment` | `true` | Whether commands see the worker's environment variables. |

The worker checks every command definition at startup and refuses to start if one is broken, for example a missing `FileName`, an invalid pattern or a placeholder with no matching parameter.

### Job data

In the dashboard, pick the worker and the job `CommandExecutionJob`. The command field lists the configured commands.

```json
{
  "command": "backup-database",
  "parameters": { "database": "orders", "target": "gcs" },
  "standardInput": null
}
```

`standardInput` is optional; its text is written to the program's standard input.

### Security

Treat this worker like a remote shell for the commands you list.

- **Only listed commands run.** Job data cannot name a program unless `AllowAdHocCommands` is on.
- **Parameters cannot inject commands.** Each argument is passed to the program as one item and never parsed by a shell, so a value like `x; rm -rf /` arrives as that literal text. A parameter job data sets that the command does not declare fails the job.
- **Give every parameter a `Pattern`.** Do this especially for values that end up in a path or in an argument the program interprets.
- **Do not put parameters inside a shell line.** If a command runs `/bin/sh -c "..."`, putting `{name}` inside that string would let a value break out. Every parameter is also exported as `MILVAION_PARAM_<NAME>`, so read it from there, quoted:

  ```json
  "Arguments": ["-c", "pg_dump \"$MILVAION_PARAM_DATABASE\" | gzip > /backups/out.gz"]
  ```

- **Limit the worker's own access.** Run it as an unprivileged user (the Docker image does) and mount only what the commands need.
- **Arguments are never logged.** Only the command name and program are, since arguments can carry secrets.

### Running it

The Docker image `milvasoft/milvaion-command-worker` contains the .NET runtime and a standard Ubuntu userland. Add your scripts and the tools they need with your own image:

```dockerfile
FROM milvasoft/milvaion-command-worker:latest
USER root
RUN apt-get update && apt-get install -y --no-install-recommends python3 postgresql-client && rm -rf /var/lib/apt/lists/*
COPY scripts/ /scripts/
COPY appsettings.json /app/appsettings.json
USER milvaion
```

The worker runs up to `JobConsumers:CommandExecutionJob:MaxParallelJobs` commands at once (default 8). Set the timeout and retries for all commands under `JobConsumers:CommandExecutionJob`, or set a timeout per job in the dashboard.
