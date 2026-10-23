using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Milvasoft.Milvaion.Sdk.Worker;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using Serilog;
using Serilog.Debugging;

// Build host
var builder = Host.CreateApplicationBuilder(args);

// Configure Serilog
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

// Register Worker SDK with auto job discovery and consumer registration
builder.Services.AddMilvaionWorkerWithJobs(builder.Configuration);

// File health check
builder.Services.AddFileHealthCheck(builder.Configuration);

// Build and run
var host = builder.Build();

return await host.RunMilvaionWorkerAsync();