# Worker SDK Reference

Complete reference documentation for the Milvaion Worker SDK.

## Table of Contents

- [Overview](#overview)
- [Installation](#installation)
- [Quick Start](#quick-start)
- [Job Interfaces](#job-interfaces)
- [Job Context](#job-context)
- [Dependency Injection](#dependency-injection)
- [Configuration](#configuration)
- [Error Handling](#error-handling)
- [Cancellation](#cancellation)
- [Logging](#logging)
- [Offline Resilience](#offline-resilience)
- [Health Checks](#health-checks)
- [Advanced Topics](#advanced-topics)

---

## Overview

The Milvaion Worker SDK enables .NET applications to execute scheduled jobs dispatched by the Milvaion Scheduler API. It provides:

- **Automatic Job Discovery** - Scans assemblies for `IJob` implementations
- **RabbitMQ Integration** - Consumes jobs with configurable parallelism
- **Redis Support** - Cancellation signals and heartbeats
- **Offline Resilience** - Local outbox pattern for disconnected scenarios
- **Configurable Retries** - Per-job retry policies with exponential backoff
- **Health Monitoring** - Automatic heartbeats and health endpoints

---

## Installation

### NuGet Package

```bash
dotnet add package Milvasoft.Milvaion.Sdk.Worker
```

Or via Package Manager Console:

```powershell
Install-Package Milvasoft.Milvaion.Sdk.Worker
```

### Project Template

For quick setup, use the project template:

```bash
# Install template
dotnet new install Milvasoft.Templates.Milvaion

# Create Console Worker
dotnet new milvaion-console-worker -n MyCompany.MyWorker

# Create API Worker (with health endpoints)
dotnet new milvaion-api-worker -n MyCompany.MyWorker
```

---

## Quick Start

### 1. Create a Job

```csharp
using System.Text.Json;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;

public class SendEmailJob : IAsyncJob
{
    private readonly IEmailService _emailService;
    private readonly ILogger<SendEmailJob> _logger;

    public SendEmailJob(IEmailService emailService, ILogger<SendEmailJob> logger)
    {
        _emailService = emailService;
        _logger = logger;
    }

    public async Task ExecuteAsync(IJobContext context)
    {
        // Log to user-friendly logs (visible in dashboard)
        context.LogInformation("Starting email job...");

        // Parse job data
        var data = JsonSerializer.Deserialize<EmailJobData>(context.Job.JobData ?? "{}");

        // Check for cancellation
        context.CancellationToken.ThrowIfCancellationRequested();

        // Execute business logic
        await _emailService.SendAsync(data.To, data.Subject, data.Body, context.CancellationToken);

        context.LogInformation($"Email sent to {data.To}");
    }
}

public class EmailJobData
{
    public string To { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
}
```

### 2. Configure the Worker

**appsettings.json:**
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information"
    }
  },
  "Worker": {
    "WorkerId": "email-worker",
    "MaxParallelJobs": 10,
    "ExecutionTimeoutSeconds": 300,
    "RabbitMQ": {
      "Host": "localhost",
      "Port": 5672,
      "Username": "guest",
      "Password": "guest",
      "VirtualHost": "/"
    },
    "Redis": {
      "ConnectionString": "localhost:6379",
      "Password": "",
      "Database": 0,
      "CancellationChannel": "Milvaion:JobScheduler:cancellation_channel"
    },
    "Heartbeat": {
      "Enabled": true,
      "IntervalSeconds": 5
    }
  },
  "JobConsumers": {
    "SendEmailJob": {
      "ConsumerId": "email-consumer",
      "MaxParallelJobs": 20,
      "ExecutionTimeoutSeconds": 120,
      "MaxRetries": 3,
      "BaseRetryDelaySeconds": 10,
      "LogUserFriendlyLogsViaLogger": true
    }
  }
}
```

### 3. Register Services

**Program.cs:**
```csharp
using Microsoft.Extensions.Hosting;
using Milvasoft.Milvaion.Sdk.Worker.Extensions;

var builder = Host.CreateApplicationBuilder(args);

// Register your services
builder.Services.AddScoped<IEmailService, SmtpEmailService>();

// Register Milvaion Worker SDK
builder.Services.AddMilvaionWorkerWithJobs(builder.Configuration);

var host = builder.Build();
await host.RunAsync();
```

### 4. Run the Worker

```bash
dotnet run
```

---

## Job Interfaces

The SDK provides four job interfaces:

| Interface | Async | Returns Result | Use Case |
|-----------|-------|----------------|----------|
| `IJob` | No | No | Simple synchronous operations |
| `IJobWithResult` | No | Yes | Sync operations returning data |
| `IAsyncJob` | Yes | No | **Recommended** for most jobs |
| `IAsyncJobWithResult` | Yes | Yes | Async operations returning data |

### IAsyncJob (Recommended)

```csharp
public interface IAsyncJob
{
    Task ExecuteAsync(IJobContext context);
}
```

**Example:**
```csharp
public class ProcessOrderJob : IAsyncJob
{
    public async Task ExecuteAsync(IJobContext context)
    {
        var orderId = GetOrderId(context);
        await ProcessOrderAsync(orderId, context.CancellationToken);
    }
}
```

### IAsyncJobWithResult

```csharp
public interface IAsyncJobWithResult
{
    Task<object?> ExecuteAsync(IJobContext context);
}
```

**Example:**
```csharp
public class GenerateReportJob : IAsyncJobWithResult
{
    public async Task<object?> ExecuteAsync(IJobContext context)
    {
        var report = await GenerateReportAsync(context.CancellationToken);
        return new { ReportId = report.Id, RowCount = report.Rows.Count };
    }
}
```

### IJob (Synchronous)

```csharp
public interface IJob
{
    void Execute(IJobContext context);
}
```

> ?? **Warning:** Avoid synchronous jobs for I/O operations. They block threads and don't support cancellation properly.

---

## Job Context

The `IJobContext` provides access to job information and utilities.

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `Job` | `JobInfo` | Job metadata and data |
| `Occurrence` | `OccurrenceInfo` | Current execution info |
| `CancellationToken` | `CancellationToken` | Cancellation signal |

### Job Info

```csharp
public class JobInfo
{
    public Guid JobId { get; }
    public string JobType { get; }
    public string? JobData { get; }
    public int Version { get; }
}
```

### Occurrence Info

```csharp
public class OccurrenceInfo
{
    public Guid OccurrenceId { get; }
    public Guid CorrelationId { get; }
    public int RetryCount { get; }
    public int MaxRetries { get; }
}
```

### Logging Methods

```csharp
// Log levels
context.LogDebug("Debug message");
context.LogInformation("Info message");
context.LogWarning("Warning message");
context.LogError("Error message");

// With parameters
context.LogInformation("Processing order {OrderId}", orderId);
```

### Usage Example

```csharp
public async Task ExecuteAsync(IJobContext context)
{
    // Access job info
    var jobId = context.Job.JobId;
    var jobType = context.Job.JobType;
    var data = context.Job.JobData;

    // Access occurrence info
    var occurrenceId = context.Occurrence.OccurrenceId;
    var retryCount = context.Occurrence.RetryCount;

    // Check if this is a retry
    if (retryCount > 0)
    {
        context.LogWarning($"Retry attempt {retryCount}");
    }

    // Use cancellation token
    await DoWorkAsync(context.CancellationToken);
}
```

---

## Dependency Injection

Jobs fully support constructor injection.

### Injecting Services

```csharp
public class DataSyncJob : IAsyncJob
{
    private readonly IDataRepository _repository;
    private readonly IExternalApi _externalApi;
    private readonly ILogger<DataSyncJob> _logger;

    public DataSyncJob(
        IDataRepository repository,
        IExternalApi externalApi,
        ILogger<DataSyncJob> logger)
    {
        _repository = repository;
        _externalApi = externalApi;
        _logger = logger;
    }

    public async Task ExecuteAsync(IJobContext context)
    {
        var data = await _externalApi.FetchDataAsync(context.CancellationToken);
        await _repository.SaveAsync(data, context.CancellationToken);
    }
}
```

### Registering Services

```csharp
var builder = Host.CreateApplicationBuilder(args);

// Scoped services (per job execution)
builder.Services.AddScoped<IDataRepository, DataRepository>();

// Transient services
builder.Services.AddTransient<IValidator, Validator>();

// Singleton services (thread-safe required)
builder.Services.AddSingleton<ICache, MemoryCache>();

// HTTP clients with retry
builder.Services.AddHttpClient<IExternalApi, ExternalApiClient>()
    .AddTransientHttpErrorPolicy(p => 
        p.WaitAndRetryAsync(3, r => TimeSpan.FromSeconds(Math.Pow(2, r))));

// Database context
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// Register Worker SDK (must be last)
builder.Services.AddMilvaionWorkerWithJobs(builder.Configuration);
```

### Service Lifetime

| Lifetime | Behavior | Use For |
|----------|----------|---------|
| Scoped | New instance per job | DbContext, repositories |
| Transient | New instance per injection | Stateless services |
| Singleton | Shared across all jobs | Thread-safe caches |

---

## Configuration

### Worker Configuration

```json
{
  "Worker": {
    "WorkerId": "my-worker",
    "MaxParallelJobs": 10,
    "ExecutionTimeoutSeconds": 300,
    "RabbitMQ": { ... },
    "Redis": { ... },
    "Heartbeat": { ... },
    "OfflineResilience": { ... }
  }
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `WorkerId` | Required | Unique worker identifier |
| `MaxParallelJobs` | 10 | Max concurrent jobs (worker-level) |
| `ExecutionTimeoutSeconds` | 300 | Default timeout (5 min) |

### RabbitMQ Configuration

```json
{
  "RabbitMQ": {
    "Host": "localhost",
    "Port": 5672,
    "Username": "guest",
    "Password": "guest",
    "VirtualHost": "/"
  }
}
```

### Redis Configuration

```json
{
  "Redis": {
    "ConnectionString": "localhost:6379",
    "Password": "",
    "Database": 0,
    "CancellationChannel": "Milvaion:JobScheduler:cancellation_channel"
  }
}
```

### Per-Job Configuration

```json
{
  "JobConsumers": {
    "SendEmailJob": {
      "ConsumerId": "email-consumer",
      "MaxParallelJobs": 20,
      "ExecutionTimeoutSeconds": 120,
      "MaxRetries": 3,
      "BaseRetryDelaySeconds": 10,
      "LogUserFriendlyLogsViaLogger": true
    }
  }
}
```

| Setting | Default | Description |
|---------|---------|-------------|
| `ConsumerId` | JobName | Consumer identifier |
| `MaxParallelJobs` | Worker default | Max parallel for this job |
| `ExecutionTimeoutSeconds` | Worker default | Timeout for this job |
| `MaxRetries` | 3 | Max retry attempts |
| `BaseRetryDelaySeconds` | 10 | Base delay for exponential backoff |
| `LogUserFriendlyLogsViaLogger` | false | Also log to ILogger |

---

## Error Handling

### Transient vs Permanent Errors

| Type | Should Retry | Examples |
|------|--------------|----------|
| Transient | Yes | Network timeout, rate limit, DB connection |
| Permanent | No | Invalid data, auth failure, business rule |

### PermanentJobException

Throw to skip retries:

```csharp
public async Task ExecuteAsync(IJobContext context)
{
    var data = ParseData(context);

    if (data == null)
    {
        // This will not retry - goes directly to DLQ
        throw new PermanentJobException("Invalid job data format");
    }

    try
    {
        await ProcessAsync(data);
    }
    catch (ValidationException ex)
    {
        // Permanent failure - no retry
        throw new PermanentJobException("Validation failed", ex);
    }
    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable)
    {
        // Transient failure - will retry
        throw;
    }
}
```

### Retry Behavior

```
Attempt 1: Execute immediately
Attempt 2: Wait BaseRetryDelaySeconds × 2^0 = 10s
Attempt 3: Wait BaseRetryDelaySeconds × 2^1 = 20s
Attempt 4: Wait BaseRetryDelaySeconds × 2^2 = 40s
After max retries: Send to Dead Letter Queue
```

### Custom Error Handling

```csharp
public async Task ExecuteAsync(IJobContext context)
{
    try
    {
        await DoWorkAsync(context.CancellationToken);
    }
    catch (Exception ex) when (IsTransient(ex))
    {
        context.LogWarning($"Transient error, will retry: {ex.Message}");
        throw; // Rethrow for retry
    }
    catch (Exception ex)
    {
        context.LogError($"Permanent error: {ex.Message}");
        throw new PermanentJobException("Unrecoverable error", ex);
    }
}

private bool IsTransient(Exception ex) =>
    ex is TimeoutException ||
    ex is HttpRequestException { StatusCode: HttpStatusCode.ServiceUnavailable };
```

---

## Cancellation

### Why Handle Cancellation

Workers receive cancellation requests when:
- User cancels from dashboard
- Worker is shutting down (SIGTERM)
- Execution timeout exceeded

### Checking Cancellation

```csharp
public async Task ExecuteAsync(IJobContext context)
{
    var items = await GetItemsAsync();

    foreach (var item in items)
    {
        // Option 1: Throw if cancelled
        context.CancellationToken.ThrowIfCancellationRequested();

        // Option 2: Check and exit gracefully
        if (context.CancellationToken.IsCancellationRequested)
        {
            context.LogWarning("Cancellation requested, stopping...");
            return;
        }

        await ProcessItemAsync(item, context.CancellationToken);
    }
}
```

### Passing Token to Operations

Always pass the token to async operations:

```csharp
public async Task ExecuteAsync(IJobContext context)
{
    var ct = context.CancellationToken;

    // HTTP calls
    var response = await _httpClient.GetAsync(url, ct);

    // Database queries
    var users = await _dbContext.Users.ToListAsync(ct);

    // Delays
    await Task.Delay(1000, ct);

    // Your services
    await _myService.ProcessAsync(data, ct);
}
```

---

## Logging

### User-Friendly Logs

Logs via `context.Log*` are stored with the occurrence and visible in the dashboard:

```csharp
context.LogInformation("Starting data sync...");
context.LogInformation($"Syncing {items.Count} items");
context.LogWarning("Rate limit approaching");
context.LogError("Failed to sync item 123");
```

### Technical Logs

Use `ILogger<T>` for technical/debug logs sent to Seq/console:

```csharp
public class MyJob : IAsyncJob
{
    private readonly ILogger<MyJob> _logger;

    public MyJob(ILogger<MyJob> logger)
    {
        _logger = logger;
    }

    public async Task ExecuteAsync(IJobContext context)
    {
        // Technical log (Seq/console)
        _logger.LogDebug("Starting job with correlation {CorrelationId}", 
            context.Occurrence.CorrelationId);

        // User-friendly log (dashboard)
        context.LogInformation("Processing started...");
    }
}
```

### Both Logs

Enable `LogUserFriendlyLogsViaLogger` to send user logs to both destinations:

```json
{
  "JobConsumers": {
    "MyJob": {
      "LogUserFriendlyLogsViaLogger": true
    }
  }
}
```

---

## Offline Resilience

Workers can continue operating when disconnected from RabbitMQ.

### Configuration

```json
{
  "Worker": {
    "OfflineResilience": {
      "Enabled": true,
      "LocalStoragePath": "./worker_data",
      "SyncIntervalSeconds": 30,
      "MaxSyncRetries": 3,
      "CleanupIntervalHours": 1,
      "RecordRetentionDays": 1
    }
  }
}
```

### How It Works

1. **Outbox Pattern**: Status updates and logs stored locally
2. **Background Sync**: Periodically syncs to RabbitMQ when connected
3. **Automatic Cleanup**: Old records removed after retention period

```
???????????????????         ???????????????????
?     Worker      ?         ?    RabbitMQ     ?
?                 ?  Sync   ?                 ?
?  ?????????????  ? ?????>  ?  Status Queue   ?
?  ?  SQLite   ?  ?         ?  Log Queue      ?
?  ?  Outbox   ?  ? <?????  ?                 ?
?  ?????????????  ? (when   ?                 ?
?                 ? online) ?                 ?
???????????????????         ???????????????????
```

---

## Health Checks

### Console Worker (File-Based)

```json
{
  "Worker": {
    "HealthCheck": {
      "Enabled": true,
      "LiveFilePath": "/tmp/live",
      "ReadyFilePath": "/tmp/ready",
      "IntervalSeconds": 30
    }
  }
}
```

**Kubernetes Probes:**
```yaml
livenessProbe:
  exec:
    command: ["test", "-f", "/tmp/live"]
  periodSeconds: 10
readinessProbe:
  exec:
    command: ["test", "-f", "/tmp/ready"]
  periodSeconds: 5
```

### API Worker (HTTP Endpoints)

Use `milvaion-api-worker` template for HTTP health endpoints:

```yaml
livenessProbe:
  httpGet:
    path: /health/live
    port: 8080
readinessProbe:
  httpGet:
    path: /health/ready
    port: 8080
```

---

## Advanced Topics

### Custom Job Registration

Manually register jobs instead of auto-discovery:

```csharp
builder.Services.AddMilvaionWorker(builder.Configuration)
    .AddJob<SendEmailJob>()
    .AddJob<ProcessOrderJob>()
    .AddJob<GenerateReportJob>();
```

### Multiple Worker IDs

Run multiple logical workers in one process:

```csharp
builder.Services.AddMilvaionWorker(builder.Configuration, "email-worker")
    .AddJob<SendEmailJob>();

builder.Services.AddMilvaionWorker(builder.Configuration, "report-worker")
    .AddJob<GenerateReportJob>();
```

### Job Filters

Apply cross-cutting concerns:

```csharp
public class LoggingJobFilter : IJobFilter
{
    public async Task OnExecutingAsync(IJobContext context)
    {
        context.LogInformation("Job starting...");
    }

    public async Task OnExecutedAsync(IJobContext context, Exception? exception)
    {
        if (exception != null)
            context.LogError($"Job failed: {exception.Message}");
        else
            context.LogInformation("Job completed");
    }
}

// Register
builder.Services.AddSingleton<IJobFilter, LoggingJobFilter>();
```

### Graceful Shutdown

The SDK handles graceful shutdown automatically:

1. Stop accepting new jobs
2. Wait for running jobs to complete (with timeout)
3. Sync offline data
4. Close connections

**Configure shutdown timeout:**
```csharp
builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(30);
});
```

---

## Troubleshooting

### Job Not Executing

1. Verify `WorkerId` matches job configuration
2. Check RabbitMQ queue bindings
3. Verify job class is public
4. Check worker logs for errors

### Connection Issues

```bash
# Check RabbitMQ
curl http://localhost:15672/api/health/checks/virtual-hosts

# Check Redis
redis-cli ping
```

### Retry Not Working

1. Verify `MaxRetries > 0` in configuration
2. Ensure not throwing `PermanentJobException`
3. Check Dead Letter Queue for failed jobs

---

## Further Reading

- [Your First Worker](../portaldocs/04-your-first-worker.md)
- [Implementing Jobs](../portaldocs/05-implementing-jobs.md)
- [Configuration Reference](../portaldocs/06-configuration.md)
- [Reliability Patterns](../portaldocs/08-reliability.md)
