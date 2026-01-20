using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Milvasoft.Milvaion.Sdk.Worker;
using Milvasoft.Milvaion.Sdk.Worker.Utils;
using SqlWorker.Jobs;
using SqlWorker.Options;
using SqlWorker.Services;

// Build host
var builder = Host.CreateApplicationBuilder(args);

// Configure logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));

// Bind SQL Worker options from configuration
builder.Services.Configure<SqlWorkerOptions>(builder.Configuration.GetSection(SqlWorkerOptions.SectionKey));

// Register dynamic enum values for connection names (must be before AddMilvaionWorkerWithJobs)
var sqlWorkerConfig = builder.Configuration.GetSection(SqlWorkerOptions.SectionKey).Get<SqlWorkerOptions>();

if (sqlWorkerConfig?.Connections?.Count > 0)
{
    JobDataTypeHelper.RegisterDynamicEnumValues(SqlJobData.ConnectionsConfigKey, sqlWorkerConfig.Connections.Keys);

    Console.WriteLine($"Registered {sqlWorkerConfig.Connections.Count} SQL connection(s): {string.Join(", ", sqlWorkerConfig.Connections.Keys)}");
}
else
{
    Console.WriteLine("WARNING: No SQL connections configured in SqlExecutorConfig:Connections");
}

// Register SQL connection factory
builder.Services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();

// Register Worker SDK with auto job discovery and consumer registration
builder.Services.AddMilvaionWorkerWithJobs(builder.Configuration);

// Add health checks
builder.Services.AddFileHealthCheck(builder.Configuration);

// Build and run
var host = builder.Build();

await host.RunAsync();
