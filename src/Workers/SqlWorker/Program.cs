using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Milvasoft.Milvaion.Sdk.Worker;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using Milvasoft.Milvaion.Sdk.Worker.Utils;
using Serilog;
using Serilog.Debugging;
using SqlWorker.Jobs;
using SqlWorker.Options;
using SqlWorker.Services;

// Build host
var builder = Host.CreateApplicationBuilder(args);

// Configure logging
SelfLog.Enable(Console.Error);

builder.Services.AddSerilog((sp, loggerConfig) =>
{
    var workerOptions = sp.GetService(typeof(IOptions<WorkerOptions>)) as IOptions<WorkerOptions>;

    loggerConfig.ReadFrom.Configuration(builder.Configuration)
                .WriteTo.Console()
                .Enrich.WithProperty("Environment", Environment.GetEnvironmentVariable("MILVA_ENV") ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"))
                .Enrich.WithProperty("AppName", workerOptions?.Value?.WorkerId)
                .Enrich.WithProperty("InstanceId", workerOptions?.Value?.InstanceId);

    var seqEnabled = builder.Configuration.GetSection("Logging:Seq:Enabled").Get<bool>();

    if (seqEnabled)
    {
        var seqUri = builder.Configuration.GetSection("Logging:Seq:Uri").Get<string>();

        if (!string.IsNullOrWhiteSpace(seqUri))
            loggerConfig.WriteTo.Seq(seqUri);
    }
});

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

return await host.RunMilvaionWorkerAsync();
