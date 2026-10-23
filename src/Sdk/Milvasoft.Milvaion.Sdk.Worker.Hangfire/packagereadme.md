# Milvaion Hangfire Integration

[![license](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/Milvasoft/Milvaion/blob/master/LICENSE) 
[![NuGet](https://img.shields.io/nuget/v/Milvasoft.Milvaion.Sdk.Worker.Hangfire)](https://www.nuget.org/packages/Milvasoft.Milvaion.Sdk.Worker.Hangfire/)   
[![NuGet](https://img.shields.io/nuget/dt/Milvasoft.Milvaion.Sdk.Worker.Hangfire)](https://www.nuget.org/packages/Milvasoft.Milvaion.Sdk.Worker.Hangfire/)

**Milvaion Hangfire Integration** allows you to monitor your existing Hangfire jobs through Milvaion's dashboard without changing your scheduler. Keep using Hangfire for scheduling while gaining Milvaion's monitoring, alerting, and metrics capabilities.

---

## Features

- **Zero-Code Job Changes** - Your existing Hangfire jobs work as-is
- **Automatic Job Tracking** - Jobs are automatically tracked when executed
- **Real-time Monitoring** - Track job executions, durations, and failures in Milvaion dashboard
- **Execution Tracking** - Every job execution is recorded with status, duration, and result
- **Worker Heartbeats** - Automatic health monitoring and status updates
- **Metrics Integration** - Jobs contribute to EPM, average duration, and success rate metrics
- **Cancellation Detection** - Tracks when jobs are deleted or cancelled

---

## Installation

```bash
dotnet add package Milvasoft.Milvaion.Sdk.Worker.Hangfire
```

---

## Quick Start

### 1. Configure Services

```csharp
using Hangfire;
using Milvasoft.Milvaion.Sdk.Worker.Hangfire.Extensions;

var builder = Host.CreateApplicationBuilder(args);

// Add Milvaion Hangfire integration
builder.Services.AddMilvaionHangfireIntegration(builder.Configuration);

// Register your job classes
builder.Services.AddTransient<MyEmailJob>();

// Configure Hangfire
builder.Services.AddHangfire((sp, config) =>
{
    config.UsePostgreSqlStorage(connectionString);
    
    // Enable Milvaion filter
    config.UseMilvaion(sp);
});

builder.Services.AddHangfireServer();

var host = builder.Build();

// Register recurring jobs
using (var scope = host.Services.CreateScope())
{
    var manager = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
    
    manager.AddOrUpdate<MyEmailJob>(
        "daily-email",
        job => job.SendAsync(CancellationToken.None),
        Cron.Daily);
}

return await host.RunMilvaionWorkerAsync();
```

### 2. Configure appsettings.json

```json
{
  "Worker": {
    "WorkerId": "hangfire-worker-1",
    "RabbitMQ": {
      "Host": "localhost",
      "Port": 5672,
      "Username": "guest",
      "Password": "guest"
    },
    "Redis": {
      "ConnectionString": "localhost:6379"
    },
    "ExternalScheduler": {
      "SourceName": "Hangfire"
    }
  }
}
```

### 3. Create Jobs (Standard Hangfire Jobs)

```csharp
public class MyEmailJob(ILogger<MyEmailJob> logger)
{
    public async Task SendAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting email job...");
        
        // Your job logic here
        await SendEmailsAsync(cancellationToken);
        
        logger.LogInformation("Email job completed!");
    }
}
```

---


## Dashboard Features

External jobs appear in Milvaion dashboard with:

- Full execution history
- Duration and success rate metrics
- Limited edit capabilities (display name, description, tags)

---


## Documentation

For complete documentation, visit: [Milvaion Documentation](https://github.com/Milvasoft/milvaion)

## License

This project is licensed under the MIT License.
