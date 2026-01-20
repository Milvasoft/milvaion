using EmailWorker.Jobs;
using EmailWorker.Options;
using EmailWorker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Milvasoft.Milvaion.Sdk.Worker;
using Milvasoft.Milvaion.Sdk.Worker.Utils;

// Build host
var builder = Host.CreateApplicationBuilder(args);

// Configure logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));

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

await host.RunAsync();
