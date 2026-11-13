using CommandWorker.Jobs;
using CommandWorker.Options;
using CommandWorker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Milvasoft.Milvaion.Sdk.Worker;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using Milvasoft.Milvaion.Sdk.Worker.Utils;
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

builder.Services.Configure<CommandWorkerOptions>(builder.Configuration.GetSection(CommandWorkerOptions.SectionKey));

// Offer the configured command names in the dashboard's job data form (must be before AddMilvaionWorkerWithJobs)
var configuredCommands = builder.Configuration.GetSection(CommandWorkerOptions.SectionKey).Get<CommandWorkerOptions>()?.Commands;

JobDataTypeHelper.RegisterDynamicEnumValues(CommandJobData.CommandsConfigKey, configuredCommands?.Keys ?? Enumerable.Empty<string>());

// Register Worker SDK with auto job discovery and consumer registration
builder.Services.AddMilvaionWorkerWithJobs(builder.Configuration);

// Add health checks
builder.Services.AddFileHealthCheck(builder.Configuration);

// Build and run
var host = builder.Build();

// A broken command definition stops the worker here rather than failing the jobs that use it.
var commandOptions = host.Services.GetRequiredService<IOptions<CommandWorkerOptions>>().Value;

CommandLineBuilder.ValidateConfiguration(commandOptions);

Console.WriteLine($"CommandWorker: {commandOptions.Commands.Count} command(s) configured: {string.Join(", ", commandOptions.Commands.Keys)}");

if (commandOptions.AllowAdHocCommands)
    Console.WriteLine("WARNING: CommandWorker:AllowAdHocCommands is on. Anyone who can create a job can run any program this worker's user can.");

return await host.RunMilvaionWorkerAsync();
