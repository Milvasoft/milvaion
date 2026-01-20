---
id: your-first-worker
title: Your First Worker
sidebar_position: 4
description: Step-by-step guide to creating your first Milvaion worker.
---


# Your First Worker With .Net

This guide walks you through creating a custom .Net Milvaion worker from scratch. By the end, you'll have a working worker with a custom job that you can deploy.

## Prerequisites

- **.NET 10 SDK** installed
- **Milvaion stack running** (see [Quick Start](02-quick-start.md))
- Basic C# knowledge

## Step 1: Install the Worker Template

Milvaion provides project templates for quick setup:

```bash
dotnet new install Milvasoft.Templates.Milvaion
```

Verify installation:

```bash
dotnet new list milvaion
```

You should see:

```
Template Name            Short Name               Language  Tags
-----------------------  -----------------------  --------  -----------------------
Milvaion Api Worker      milvaion-api-worker      [C#]      Api/Worker/Milvaion
Milvaion Console Worker  milvaion-console-worker  [C#]      Console/Worker/Milvaion
```

## Step 2: Create a New Worker Project

```bash
dotnet new milvaion-console-worker -n MyCompany.BillingWorker
cd MyCompany.BillingWorker
```

This creates:

```
MyCompany.BillingWorker/
├── Program.cs                    # Entry point
├── appsettings.json              # Configuration
├── appsettings.Development.json  # Dev config
|
├── Jobs/
|    └── SampleJob.cs             # Example job
|
├── Dockerfile                    # Container build
└── MyCompany.BillingWorker.csproj
```

## Step 3: Configure the Worker

Edit `appsettings.json` to point to your Milvaion infrastructure:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "Microsoft": "Debug",
      "System": "Debug"
    }
  },
  "Worker": {
    "WorkerId": "sample-worker-01",
    "MaxParallelJobs": 128,
    "ExecutionTimeoutSeconds": 300,
    "RabbitMQ": {
      "Host": "rabbitmq",
      "Port": 5672,
      "Username": "guest",
      "Password": "guest",
      "VirtualHost": "/"
    },
    "Redis": {
      "ConnectionString": "redis:6379",
      "Password": "",
      "Database": 0,
      "CancellationChannel": "Milvaion:JobScheduler:cancellation_channel"
    },
    "Heartbeat": {
      "Enabled": true,
      "IntervalSeconds": 5
    },
    "OfflineResilience": {
      "Enabled": true,
      "LocalStoragePath": "./worker_data",
      "SyncIntervalSeconds": 30,
      "MaxSyncRetries": 3,
      "CleanupIntervalHours": 1,
      "RecordRetentionDays": 1
    }
  },
  "JobConsumers": {
    "SimpleJob": {
      "ConsumerId": "simple-consumer",
      "MaxParallelJobs": 32,
      "ExecutionTimeoutSeconds": 120,
      "MaxRetries": 3,
      "BaseRetryDelaySeconds": 5,
      "LogUserFriendlyLogsViaLogger": true
    },
    "SendEmailJob": {
      "ConsumerId": "email-consumer",
      "MaxParallelJobs": 16,
      "ExecutionTimeoutSeconds": 600,
      "MaxRetries": 3,
      "BaseRetryDelaySeconds": 5,
      "LogUserFriendlyLogsViaLogger": true
    }
  }
}
```

> **Note**: For Docker, use container names (`rabbitmq`, `redis`) instead of `localhost`.

## Step 4: Create Your First Job

Create `Jobs/GenerateInvoiceJob.cs`:

```csharp
using System.Text.Json;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;

namespace MyCompany.BillingWorker.Jobs;

public class GenerateInvoiceJob : IAsyncJob
{
    public async Task ExecuteAsync(IJobContext context)
    {
        // 1. Log start
        context.LogInformation("Starting invoice generation job");

        // 2. Parse job data
        var data = JsonSerializer.Deserialize<InvoiceJobData>(
            context.Job.JobData ?? "{}"
        );

        if (data == null || data.OrderId <= 0)
        {
            context.LogError("Invalid OrderId");
            throw new ArgumentException("OrderId is required");
        }

        context.LogInformation($"Generating invoice for OrderId: {data.OrderId}");

        // 3. Cancellation check
        context.CancellationToken.ThrowIfCancellationRequested();

        // 4. Simulate invoice generation
        await Task.Delay(3000, context.CancellationToken);

        // 5. Finish
        context.LogInformation($"Invoice successfully generated for OrderId: {data.OrderId}");
    }
}

public class InvoiceJobData
{
    public int OrderId { get; set; }
    public string Currency { get; set; } = "USD";
}

```

## Step 5: Register the Job

Add configuration for your new job in `appsettings.json`:

```json
{
  "JobConsumers": {
    "GenerateInvoiceJob": {
      "ConsumerId": "invoice-consumer",
      "MaxParallelJobs": 8,
      "ExecutionTimeoutSeconds": 300,
      "MaxRetries": 5,
      "BaseRetryDelaySeconds": 10,
      "LogUserFriendlyLogsViaLogger": true
    }
  }
}
```

The SDK **automatically discovers** jobs that implement `IJob`, `IAsyncJob`, `IJobWithResult`, or `IAsyncJobWithResult`.

## Step 6: Run the Worker

### Locally

```bash
dotnet run
```

Expected output:

```
info: Milvaion.Sdk.Worker[0]
      Registered job: GenerateInvoiceJob → MyCompany.BillingWorker.Jobs.GenerateInvoiceJob
info: Milvaion.Worker.Job[0]
      Starting invoice generation job
info: Milvaion.Worker.Job[0]
      Generating invoice for OrderId: 12345
info: Milvaion.Worker.Job[0]
      Invoice successfully generated for OrderId: 12345
```

### With Docker

Build and run:

```bash
docker build -t my-billing-worker .

docker run -d --name billing-worker \
  --network milvaion_default \
  -e Worker__RabbitMQ__Host=milvaion-rabbitmq \
  -e Worker__Redis__ConnectionString=milvaion-redis:6379 \
  my-billing-worker
```

## Step 7: Test Your Job

### Create the Job via API

```bash
curl -X POST http://localhost:5000/api/v1/jobs/job \
  -H "Content-Type: application/json" \
  -d '{
    "displayName": "Generate Invoice",
    "workerId": "billing-worker",
    "selectedJobName": "GenerateInvoiceJob",
    "cronExpression": "0 */5 * * * *",
    "isActive": true,
    "jobData": "{\"orderId\": 12345, \"currency\": \"EUR\"}"
  }'
```

### Trigger Immediately

```bash
curl -X POST http://localhost:5000/api/v1/jobs/job/trigger \
  -H "Content-Type: application/json" \
  -d '{"jobId": "YOUR_JOB_ID", "reason": "Testing", "force": true}'
```

### Watch Execution

```bash
# Worker logs
docker logs -f billing-worker

# Or if running locally
dotnet run
```

### Check in Dashboard

1. Open **http://localhost:5000**
2. Go to **Jobs** → Click your job
3. See **Execution History** with logs

## Understanding the Code

### Program.cs (Entry Point)

```csharp
using Microsoft.Extensions.Hosting;
using Milvasoft.Milvaion.Sdk.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Register Worker SDK - auto-discovers IJob implementations
builder.Services.AddMilvaionWorkerWithJobs(builder.Configuration);

var host = builder.Build();
await host.RunAsync();
```

### IJobContext

Every job receives a context object:

```csharp
public interface IJobContext
{
    // Unique ID for this execution (for tracing)
    Guid CorrelationId { get; }
    
    // The job definition (name, data, etc.)
    ScheduledJob Job { get; }
    
    // Which worker is running this
    string WorkerId { get; }
    
    // Cancel when shutdown requested
    CancellationToken CancellationToken { get; }
    
    // Logging methods (logs go to dashboard)
    void LogInformation(string message);
    void LogWarning(string message);
    void LogError(string message, Exception ex = null);
}
```

### Job Data

Jobs receive data as JSON in `context.Job.JobData`:

```csharp
// Define a strongly-typed class
public class MyJobData
{
    public string CustomerId { get; set; }
    public int OrderId { get; set; }
}

// Deserialize in your job
var data = JsonSerializer.Deserialize<MyJobData>(context.Job.JobData ?? "{}");
```

## Adding Dependency Injection

Jobs support constructor injection:

```csharp
public class GenerateInvoiceJob : IAsyncJob
{
    private readonly IBillingService _billingService;
    private readonly ILogger<GenerateInvoiceJob> _logger;
    
    public GenerateInvoiceJob(IBillingService billingService, ILogger<GenerateInvoiceJob> logger)
    {
        _logger = billingService;
        _logger = logger;
    }
    
    public async Task ExecuteAsync(IJobContext context)
    {
        var data = JsonSerializer.Deserialize<InvoiceJobData>(context.Job.JobData ?? "{}");
        
        await billingService.SendAsync(data.To, data.Subject, data.Body);
        
        context.LogInformation("Invoice successfully generated!");
    }
}
```

Register services in `Program.cs`:

```csharp

var builder = Host.CreateApplicationBuilder(args);

// Register your services
builder.Services.AddScoped<IBillingService, InvoiceService>();

// Register Worker SDK
builder.Services.AddMilvaionWorkerWithJobs(builder.Configuration);

var host = builder.Build();
await host.RunAsync();

```

## Common Patterns

### Handling Cancellation

Always check `CancellationToken` for graceful shutdown:

```csharp
public async Task ExecuteAsync(IJobContext context)
{
    var items = await GetItemsAsync();
    
    foreach (var item in items)
    {
        // Check before each iteration
        context.CancellationToken.ThrowIfCancellationRequested();
        
        await ProcessItemAsync(item, context.CancellationToken);
    }
}
```

### Returning Results

Use `IAsyncJobWithResult` to return data:

```csharp
public class GenerateReportJob : IAsyncJobWithResult
{
    public async Task<string> ExecuteAsync(IJobContext context)
    {
        var report = await GenerateReportAsync();
        
        // Return JSON result (stored in occurrence.Result)
        return JsonSerializer.Serialize(new { 
            ReportId = report.Id,
            RowCount = report.RowCount 
        });
    }
}
```

### Error Handling

Throw exceptions to trigger retry logic:

```csharp
public async Task ExecuteAsync(IJobContext context)
{
    try
    {
        await _externalApi.CallAsync();
    }
    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable)
    {
        // Transient error - will retry
        context.LogWarning("Service unavailable, will retry");
        throw;
    }
    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
    {
        // Permanent error - log and don't retry pointlessly
        context.LogError("Invalid request data", ex);
        throw;
    }
}
```
---

## Converting an Existing Project to a Worker

If you already have a .NET project and want to add Milvaion Worker functionality, follow these steps:

### Step 1: Install the Worker SDK Package

Add the `Milvaion.Sdk.Worker` NuGet package to your existing project:

```bash
dotnet add package Milvaion.Sdk.Worker
```

### Step 2: Add Worker Configuration

Add the Worker SDK configuration to your existing `appsettings.json`:

```json
{
  // ...your existing configuration...
  
  "Worker": {
    "WorkerId": "my-existing-app-worker",
    "MaxParallelJobs": 32,
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
      "IntervalSeconds": 10
    },
    "OfflineResilience": {
      "Enabled": true,
      "LocalStoragePath": "./worker_data",
      "SyncIntervalSeconds": 30,
      "MaxSyncRetries": 3,
      "CleanupIntervalHours": 24,
      "RecordRetentionDays": 7
    }
  },
  "JobConsumers": {
    "YourJobName": {
      "ConsumerId": "your-job-consumer",
      "MaxParallelJobs": 16,
      "ExecutionTimeoutSeconds": 120,
      "MaxRetries": 3,
      "BaseRetryDelaySeconds": 5,
      "LogUserFriendlyLogsViaLogger": true
    }
  }
}
```

### Step 3: Register Worker SDK

Register Worker services to your existing startup configuration:

```csharp
using Milvaion.Sdk.Worker;

var builder = WebApplication.CreateBuilder(args);

// Your existing service registrations

// Add Worker SDK with automatic job discovery
builder.Services.AddMilvaionWorkerWithJobs(builder.Configuration);

var app = builder.Build();

// Your existing configurations

await host.RunAsync();
```

## What's Next?

- **[Implementing Jobs](05-implementing-jobs.md)** - Advanced job patterns
- **[Configuration](06-configuration.md)** - All configuration options
- **[Deployment](07-deployment.md)** - Production deployment guide
