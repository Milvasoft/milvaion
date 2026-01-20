# ApiWorker

A Milvaion API worker project for executing scheduled jobs from the Milvaion Scheduler API with built-in REST API endpoints.

## Getting Started

This project was created from the **Milvaion API Worker** template.

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

The API will start on:
- HTTP: `http://localhost:5000`
- HTTPS: `https://localhost:5001`

### Available Endpoints

#### Health Check
```
GET /offline-storage-stats
```

Returns offline storage statistics including:
- Total pending logs
- Total pending status updates
- Oldest record timestamps

Example response:
```json
{
  "totalLogs": 42,
  "totalStatusUpdates": 15,
  "oldestLogTimestamp": "2024-01-08T10:30:00Z",
  "oldestStatusUpdateTimestamp": "2024-01-08T10:35:00Z"
}
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

### Adding Custom Endpoints

Since this is an ASP.NET Core Web API, you can add custom endpoints in `Program.cs`:

```csharp
app.MapGet("/health", () => "OK")
   .WithName("Health");

app.MapPost("/trigger-job", async (string jobId) =>
{
    // Custom job triggering logic
    return Results.Ok(new { jobId, status = "triggered" });
})
.WithName("TriggerJob");
```

### Docker Deployment

Build and run with Docker:

```bash
docker build -t my-api-worker .
docker run -d -p 5000:8080 my-api-worker
```

### Documentation

- [Milvaion Documentation](https://github.com/Milvasoft/milvaion)
- [Worker SDK Guide](https://www.nuget.org/packages/Milvasoft.Milvaion.Sdk.Worker)
- [ASP.NET Core Minimal APIs](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis)

### License

MIT License
