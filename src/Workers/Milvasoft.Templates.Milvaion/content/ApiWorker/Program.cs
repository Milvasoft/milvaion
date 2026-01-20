using Microsoft.Extensions.Options;
using Milvasoft.Milvaion.Sdk.Worker;
using Milvasoft.Milvaion.Sdk.Worker.HealthChecks;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using Milvasoft.Milvaion.Sdk.Worker.Persistence;

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();
    builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));

    // Add services to the container.
    builder.Services.AddOpenApi();

    // Add health checks
    builder.Services.AddHealthChecks()
                    .AddCheck<RedisHealthCheck>("Redis", tags: ["redis", "cache"])
                    .AddCheck<RabbitMQHealthCheck>("RabbitMQ", tags: ["rabbitmq", "messaging"]);

    // Register Worker SDK with auto job discovery and consumer registration
    builder.Services.AddMilvaionWorkerWithJobs(builder.Configuration);

    // Register health check endpoints
    builder.Services.AddHealthCheckEndpoints(builder.Configuration);

    var app = builder.Build();

    // Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
        app.MapOpenApi();

    app.UseHttpsRedirection();

    // Get offline storage statistics
    app.MapGet("/offline-storage-stats", async (IOptions<WorkerOptions> options, ILoggerFactory loggerFactory) =>
    {
        using var localStorage = new LocalStateStore(options.Value.OfflineResilience.LocalStoragePath, loggerFactory);

        return await localStorage.GetStatsAsync();
    }).WithName("OfflineStorageStats");

    app.UseHealthCheckEndpoints(builder.Configuration);

    await app.RunAsync();
}
catch (Exception ex)
{
    Console.WriteLine($"Fatal error: {ex.Message}");
    throw;
}

