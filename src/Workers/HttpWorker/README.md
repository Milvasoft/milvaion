# MilvaionWorker

A Milvaion console worker project for executing scheduled jobs from the Milvaion Scheduler API.

## Getting Started

This project was created from the **Milvaion Console Worker** template.

### Prerequisites

- .NET 10.0 SDK or later
- Access to RabbitMQ instance
- Access to Redis instance
- Milvaion Scheduler API running

### Configuration

Update `appsettings.json` with your infrastructure settings:

```json
{
  "Worker": {
    "WorkerId": "my-worker-01",
    "RabbitMQ": {
      "Host": "localhost",
      "Port": 5672,
      "Username": "guest",
      "Password": "guest"
    },
    "Redis": {
      "ConnectionString": "localhost:6379"
    }
  }
}
```

### Running the Worker

```bash
dotnet run
```

Or with Docker:

```bash
docker build -t milvaion-sampleworker .

docker run -d --name worker milvaion-sampleworker
```
or with default Milvaion Network:
```bash
docker run --name worker --network milvaion_milvaion-network milvaion-sampleworker
```

### Adding New Jobs

1. Create a new class in the `Jobs/` folder
2. Implement `IAsyncJob` interface
3. Add configuration to `appsettings.json` under `JobConsumers`

Example:

```csharp
public class MyCustomJob : IAsyncJob
{
    public async Task ExecuteAsync(IJobContext context)
    {
        context.LogInformation("Job started!");
        
        // Your business logic here
        
        context.LogInformation("Job completed!");
    }
}
```

Add to `appsettings.json`:

```json
{
  "JobConsumers": {
    "MyCustomJob": {
      "ConsumerId": "mycustom-consumer",
      "MaxParallelJobs": 10,
      "ExecutionTimeoutSeconds": 300,
      "MaxRetries": 3
    }
  }
}
```

### Documentation

- [Milvaion Documentation](https://github.com/Milvasoft/milvaion-api)
- [Worker SDK Guide](https://www.nuget.org/packages/Milvasoft.Milvaion.Sdk.Worker)

### License

MIT License
