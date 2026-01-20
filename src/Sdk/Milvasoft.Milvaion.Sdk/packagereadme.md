# Milvaion Worker SDK

[![license](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/Milvasoft/Milvaion/blob/master/LICENSE) 
[![NuGet](https://img.shields.io/nuget/v/Milvasoft.Milvaion.Sdk.Worker)](https://www.nuget.org/packages/Milvasoft.Milvaion.Sdk.Worker/)   
[![NuGet](https://img.shields.io/nuget/dt/Milvasoft.Milvaion.Sdk.Worker)](https://www.nuget.org/packages/Milvasoft.Milvaion.Sdk.Worker/)

**Milvaion Worker SDK** is a .NET library that enables your applications to execute scheduled jobs dispatched by the Milvaion Scheduler API. It provides automatic job discovery, RabbitMQ integration, offline resilience, health monitoring, and comprehensive retry mechanisms.

---

## Features

- **Automatic Job Discovery** - Scans your assembly for `IJob` implementations and registers them automatically  
- **RabbitMQ Integration** - Consumes jobs from RabbitMQ queues with configurable parallelism  
- **Redis Support** - Uses Redis for job cancellation signals and worker heartbeats  
- **Offline Resilience** - Local outbox pattern for storing logs/status updates when disconnected  
- **Configurable Retries** - Per-job retry policies with exponential backoff  
- **Health Monitoring** - Automatic worker heartbeats and zombie detection  
- **Parallel Execution** - Control concurrency per worker and per job type  
- **Graceful Shutdown** - Proper cleanup and in-flight job handling on termination

---

## Installation

Install the NuGet package:

```bash
dotnet add package Milvasoft.Milvaion.Sdk.Worker
```

Or via Package Manager Console:

```powershell
Install-Package Milvasoft.Milvaion.Sdk.Worker
```

---

## Quick Start

### 1. Create a Job Class

Implement the `IJob` interface:

```csharp
using Milvaion.Sdk.Worker.Abstractions;

public class SendEmailJob : IJob
{
    public async Task<JobExecutionResult> ExecuteAsync(IJobContext context)
    {
        var email = context.GetData<string>("email");
        
        context.LogInformation($"Sending email to {email}");
        
        // Your business logic here
        await Task.Delay(1000);
        
        return JobExecutionResult.Success("Email sent successfully");
    }
}
```

### 2. Configure `appsettings.json`

```json
{
  "Worker": {
    "WorkerId": "email-worker",
    "MaxParallelJobs": 5,
    "RabbitMQ": {
      "Host": "localhost",
      "Port": 5672,
      "Username": "guest",
      "Password": "guest",
      "VirtualHost": "/"
    },
    "Redis": {
      "ConnectionString": "localhost:6379",
      "Database": 0
    },
    "Heartbeat": {
      "Enabled": true,
      "IntervalSeconds": 30
    },
    "OfflineResilience": {
      "Enabled": true,
      "LocalStoragePath": "./worker_data"
    }
  },
  "JobConsumers": {
    "SendEmailJob": {
      "ConsumerId": "sendemail-consumer",
      "MaxParallelJobs": 3,
      "ExecutionTimeoutSeconds": 300,
      "MaxRetries": 3,
      "BaseRetryDelaySeconds": 60,
      "RoutingPatterns": ["sendemail.*"]
    }
  }
}
```

### 3. Register Worker Services in `Program.cs`

```csharp
using Microsoft.Extensions.Hosting;
using Milvaion.Sdk.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Auto-discover jobs and register worker services
builder.Services.AddMilvaionWorkerWithJobs(builder.Configuration);

var host = builder.Build();
await host.RunAsync();
```

---

## Configuration Reference

### Worker Options

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `WorkerId` | `string` | `"worker"` | Unique identifier for this worker instance |
| `MaxParallelJobs` | `int` | `10` | Maximum concurrent job executions |
| `RabbitMQ.Host` | `string` | `"localhost"` | RabbitMQ server hostname |
| `RabbitMQ.Port` | `int` | `5672` | RabbitMQ server port |
| `RabbitMQ.Username` | `string` | `"guest"` | RabbitMQ username |
| `RabbitMQ.Password` | `string` | `"guest"` | RabbitMQ password |
| `Redis.ConnectionString` | `string` | `"localhost:6379"` | Redis connection string |
| `Heartbeat.Enabled` | `bool` | `true` | Enable worker heartbeat |
| `Heartbeat.IntervalSeconds` | `int` | `30` | Heartbeat interval |
| `OfflineResilience.Enabled` | `bool` | `false` | Enable outbox pattern |

### Job Consumer Options

Each job can have dedicated configuration under `JobConsumers`:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ConsumerId` | `string` | **Required** | Unique consumer identifier |
| `MaxParallelJobs` | `int` | (inherits) | Job-specific concurrency limit |
| `ExecutionTimeoutSeconds` | `int` | `300` | Maximum execution time |
| `MaxRetries` | `int` | `3` | Number of retry attempts |
| `BaseRetryDelaySeconds` | `int` | `60` | Initial retry delay (exponential backoff) |
| `RoutingPatterns` | `string[]` | Auto-generated | RabbitMQ routing key patterns |

---

## Job Lifecycle

1. **Discovery** - SDK scans assembly for `IJob` implementations on startup
2. **Validation** - Ensures each job has corresponding configuration
3. **Consumer Registration** - Creates dedicated RabbitMQ consumer per job type
4. **Execution** - Receives message, deserializes context, executes job logic
5. **Status Updates** - Publishes `Running`, `Completed`, or `Failed` status to API
6. **Retry Logic** - On failure, republishes message with incremented retry count
7. **DLQ Handling** - Moves to Dead Letter Queue after max retries exhausted

---

## Offline Resilience

When enabled, the SDK stores logs and status updates locally when the central API is unreachable:

```json
{
  "OfflineResilience": {
    "Enabled": true,
    "LocalStoragePath": "./worker_data",
    "SyncIntervalSeconds": 60
  }
}
```

- **Outbox Pattern**: Persists updates to SQLite database
- **Auto-Sync**: Periodically retries sending stored entries
- **At-Least-Once Guarantee**: Ensures no data loss during network failures

---

## Job Cancellation

Jobs can be cancelled via Redis pub/sub:

```csharp
public class LongRunningJob : IJob
{
    public async Task<JobExecutionResult> ExecuteAsync(IJobContext context)
    {
        for (int i = 0; i < 100; i++)
        {
            // Check cancellation token
            context.CancellationToken.ThrowIfCancellationRequested();
            
            await Task.Delay(1000, context.CancellationToken);
        }
        
        return JobExecutionResult.Success();
    }
}
```

---

## Best Practices

- **Set Job-Specific Timeouts** - Different jobs have different execution profiles  
- **Use Structured Logging** - Leverage `context.LogInformation()` for traceable logs  
- **Handle Transient Failures** - Design idempotent jobs for safe retries  
- **Monitor Worker Health** - Use heartbeat and health endpoints  
- **Scale Horizontally** - Deploy multiple worker instances with same `WorkerId`  
- **Enable Offline Resilience** - For unreliable network environments  

---

## Examples

### Complete Worker Project

See the [SampleWorker](https://github.com/Milvasoft/Milvaion/tree/main/src/Workers/SampleWorker) project for a reference implementation.

### Job with Retries

```csharp
public class UnreliableApiJob : IJob
{
    private readonly HttpClient _httpClient;
    
    public UnreliableApiJob(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }
    
    public async Task<JobExecutionResult> ExecuteAsync(IJobContext context)
    {
        try
        {
            var response = await _httpClient.GetAsync(context.GetData<string>("url"));
            response.EnsureSuccessStatusCode();
            
            return JobExecutionResult.Success();
        }
        catch (HttpRequestException ex)
        {
            // Will automatically retry based on MaxRetries config
            return JobExecutionResult.Failure($"API call failed: {ex.Message}");
        }
    }
}
```

Configuration:

```json
{
  "JobConsumers": {
    "UnreliableApiJob": {
      "ConsumerId": "api-consumer",
      "MaxRetries": 5,
      "BaseRetryDelaySeconds": 30
    }
  }
}
```

---

## Documentation

- [Full Documentation](https://portal.milvasoft.com/docs/1.0.1/open-source-libs/milvaion/milvaion-doc-guide)
- [Quick Start Guide](https://portal.milvasoft.com/docs/quickstart)
- [Configuration Reference](https://portal.milvasoft.com/docs/configuration)
- [API Reference](https://portal.milvasoft.com/docs/api-reference)

---

## Support

- [Report Issues](https://github.com/Milvasoft/Milvaion/issues)
- [Discussions](https://github.com/Milvasoft/Milvaion/discussions)
- Email: support@milvasoft.com

---

## License

Licensed under the [MIT License](https://github.com/Milvasoft/Milvaion/blob/master/LICENSE).

---

**Built with love by [Milvasoft](https://milvasoft.com)**
