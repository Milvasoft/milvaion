Milvaion Worker Templates
  

  
[![license](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/Milvasoft/Milvaion/blob/master/LICENSE) 
[![NuGet](https://img.shields.io/nuget/v/Milvasoft.Milvaion.Templates)](https://www.nuget.org/packages/Milvasoft.Milvaion.Templates/)   
[![NuGet](https://img.shields.io/nuget/dt/Milvasoft.Milvaion.Templates)](https://www.nuget.org/packages/Milvasoft.Milvaion.Templates/)

This package contains project templates for creating Milvaion Workers.

![milva-bird](https://user-images.githubusercontent.com/13048645/141461853-dbacad32-2150-4276-a848-45b81f2eeeb2.jpg)

# Templates

## Console Worker Template
**Short Name:** `milvaion-worker`

A console-based worker for executing scheduled jobs in background processes.

### Features
- Background console application
- Job discovery and auto-registration
- RabbitMQ job consumer
- Redis integration for cancellation signals
- Offline resilience with outbox pattern
- Docker support

### Usage
```bash
dotnet new milvaion-worker -n MyWorker
```

## API Worker Template
**Short Name:** `milvaion-api-worker`

An ASP.NET Core Web API worker with REST endpoints for monitoring and management.

### Features
- All Console Worker features
- Built-in REST API endpoints
- Health check endpoint
- Offline storage statistics endpoint
- OpenAPI/Swagger support
- Easy to add custom endpoints

### Usage
```bash
dotnet new milvaion-api-worker -n MyApiWorker
```

# Installation

Install the template package:

```bash
dotnet new install Milvasoft.Milvaion.Templates
```

# Available Templates

After installation, you'll see both templates:

```bash
dotnet new list milvaion

Template Name            Short Name             Language  Tags
-----------------------  ---------------------  --------  -----------------------
Milvaion Console Worker  milvaion-worker        [C#]      Console/Worker/Milvaion
Milvaion Api Worker      milvaion-api-worker    [C#]      Api/Worker/Milvaion
```

# Quick Start

## Create a Console Worker

```bash
# Create new console worker
dotnet new milvaion-worker -n EmailWorker

# Navigate to project
cd EmailWorker

# Run the worker
dotnet run
```

## Create an API Worker

```bash
# Create new API worker
dotnet new milvaion-api-worker -n ReportWorker

# Navigate to project
cd ReportWorker

# Run the API worker
dotnet run

# Access API at http://localhost:5000
# Check storage stats: http://localhost:5000/offline-storage-stats
```

# Template Options

Both templates support the following options:

## Framework Selection
```bash
# Use .NET 9.0
dotnet new milvaion-worker -n MyWorker --Framework net9.0

# Use .NET 10.0 (default)
dotnet new milvaion-worker -n MyWorker --Framework net10.0
```

## Nullable Context
```bash
# Enable nullable (default: disable)
dotnet new milvaion-worker -n MyWorker --Nullable enable

# Disable nullable
dotnet new milvaion-worker -n MyWorker --Nullable disable
```

# Configuration

Both templates use the same configuration structure in `appsettings.json`:

```json
{
  "Worker": {
    "WorkerId": "my-worker",
    "MaxParallelJobs": 128,
    "RabbitMQ": {
      "Host": "localhost",
      "Port": 5672,
      "Username": "guest",
      "Password": "guest"
    },
    "Redis": {
      "ConnectionString": "localhost:6379"
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
    "SimpleJob": {
      "ConsumerId": "simple-consumer",
      "MaxParallelJobs": 32,
      "ExecutionTimeoutSeconds": 120,
      "MaxRetries": 3
    }
  }
}
```

# Adding Jobs

Create a new job class implementing `IAsyncJob`:

```csharp
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;

public class MyJob : IAsyncJob
{
    public async Task ExecuteAsync(IJobContext context)
    {
        context.LogInformation("Job started!");
        
        // Your business logic here
        await Task.Delay(1000, context.CancellationToken);
        
        context.LogInformation("Job completed!");
    }
}
```

Add configuration to `appsettings.json`:

```json
{
  "JobConsumers": {
    "MyJob": {
      "ConsumerId": "my-consumer",
      "MaxParallelJobs": 10,
      "ExecutionTimeoutSeconds": 300,
      "MaxRetries": 3
    }
  }
}
```

# Docker Support

Both templates include Dockerfile:

```bash
# Build image
docker build -t my-worker .

# Run container
docker run -d \
  --name my-worker \
  --network milvaion-network \
  -e Worker__WorkerId=worker-01 \
  -e Worker__RabbitMQ__Host=rabbitmq \
  my-worker
```

# API Worker Endpoints

The API Worker template includes these endpoints:

## Storage Statistics
```
GET /offline-storage-stats
```

Returns information about pending logs and status updates.

## OpenAPI
```
GET /openapi/v1.json
```

OpenAPI specification (in Development mode).

# Uninstall

To uninstall the templates:

```bash
dotnet new uninstall Milvasoft.Milvaion.Templates
```

# Documentation

- [Milvaion Documentation](https://github.com/Milvasoft/milvaion-api)
- [Worker SDK Guide](https://www.nuget.org/packages/Milvasoft.Milvaion.Sdk.Worker)
- [.NET Templates](https://learn.microsoft.com/en-us/dotnet/core/tools/custom-templates)

# Support

- ?? [Report Issues](https://github.com/Milvasoft/Milvaion/issues)
- ?? [Discussions](https://github.com/Milvasoft/Milvaion/discussions)
- ?? Email: support@milvasoft.com

# License

Licensed under the [MIT License](https://github.com/Milvasoft/Milvaion/blob/master/LICENSE).

---

**Built with ?? by [Milvasoft](https://milvasoft.com)**
