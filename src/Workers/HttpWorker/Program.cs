using HttpWorker.Jobs;
using HttpWorker.Options;
using HttpWorker.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Milvasoft.Milvaion.Sdk.Worker;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using Serilog;
using Serilog.Debugging;

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

builder.Services.Configure<HttpWorkerOptions>(builder.Configuration.GetSection(HttpWorkerOptions.SectionKey));
builder.Services.AddSingleton<HttpTargetPolicy>();

// Register HttpClient with connection pooling for standard requests
builder.Services.AddHttpClient(HttpRequestSenderJob.DefaultClientName).ConfigurePrimaryHttpMessageHandler(sp =>
{
    var handler = new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        MaxConnectionsPerServer = 100,
        EnableMultipleHttp2Connections = true,
        AutomaticDecompression = System.Net.DecompressionMethods.All
    };

    sp.GetRequiredService<HttpTargetPolicy>().Apply(handler);

    return handler;
});

// Register Worker SDK with auto job discovery and consumer registration
builder.Services.AddMilvaionWorkerWithJobs(builder.Configuration);

// Add health checks
builder.Services.AddFileHealthCheck(builder.Configuration);

// Build and run
var host = builder.Build();

// Resolved here so an invalid CIDR stops the worker at startup rather than failing its first job
if (!host.Services.GetRequiredService<HttpTargetPolicy>().RestrictsTargets)
    Console.WriteLine("WARNING: HttpWorker:Security sets no target restrictions. Jobs can reach anything this worker can, including internal services and cloud metadata endpoints.");

return await host.RunMilvaionWorkerAsync();
