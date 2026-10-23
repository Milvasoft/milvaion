using EmailWorker.Jobs;
using EmailWorker.Options;
using EmailWorker.Services;
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

// Configure Email Worker options
builder.Services.Configure<EmailWorkerOptions>(builder.Configuration.GetSection(EmailWorkerOptions.SectionKey));

// Register dynamic enum values for SMTP config names (must be before AddMilvaionWorkerWithJobs)
var emailWorkerConfig = builder.Configuration.GetSection(EmailWorkerOptions.SectionKey).Get<EmailWorkerOptions>();

if (emailWorkerConfig?.SmtpConfigs?.Count > 0)
{
    JobDataTypeHelper.RegisterDynamicEnumValues(EmailJobData.SmtpConfigsKey, emailWorkerConfig.SmtpConfigs.Keys);

    Console.WriteLine($"Registered {emailWorkerConfig.SmtpConfigs.Count} SMTP configuration(s): {string.Join(", ", emailWorkerConfig.SmtpConfigs.Keys)}");
}
else
{
    Console.WriteLine("WARNING: No SMTP configurations found in EmailConfig:SmtpConfigs");
}

// Register email sender
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();

// Register Worker SDK with auto job discovery and consumer registration
builder.Services.AddMilvaionWorkerWithJobs(builder.Configuration);

// Add health checks
builder.Services.AddFileHealthCheck(builder.Configuration);

// Build and run
var host = builder.Build();

return await host.RunMilvaionWorkerAsync();
