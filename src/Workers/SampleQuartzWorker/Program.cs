using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Milvasoft.Milvaion.Sdk.Worker;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using Milvasoft.Milvaion.Sdk.Worker.Quartz.Extensions;
using Quartz;
using SampleQuartzWorker.Jobs;
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

// Add Milvaion Quartz integration
// This registers all core worker services (heartbeat, status updates, etc.)
// AND Quartz-specific listeners for job monitoring
builder.Services.AddMilvaionQuartzIntegration(builder.Configuration);

// Configure Quartz with sample jobs
builder.Services.AddQuartz(q =>
{
    // Use Milvaion listeners for job monitoring
    q.UseMilvaion();

    // Register SampleLogJob - runs every 30 seconds
    var sampleLogJobKey = new JobKey("SampleLogJob", "Samples");
    q.AddJob<SampleLogJob>(opts => opts
        .WithIdentity(sampleLogJobKey)
        .WithDescription("Sample job that logs messages periodically"));

    q.AddTrigger(opts => opts
        .ForJob(sampleLogJobKey)
        .WithIdentity("SampleLogJob-Trigger")
        .WithCronSchedule("0/30 * * * * ?") // Every 30 seconds
        .WithDescription("Triggers SampleLogJob every 30 seconds"));

    // Register SendEmailJob - runs every minute
    var sendEmailJobKey = new JobKey("SendEmailJob", "Samples");
    q.AddJob<SendEmailJob>(opts => opts
        .WithIdentity(sendEmailJobKey)
        .WithDescription("Sample job that simulates sending emails")
        .UsingJobData("Recipient", "test@example.com")
        .UsingJobData("Subject", "Scheduled Notification"));

    q.AddTrigger(opts => opts
        .ForJob(sendEmailJobKey)
        .WithIdentity("SendEmailJob-Trigger")
        .WithCronSchedule("0/10 * * * * ?") // Every 10 sec
        .WithDescription("Triggers SendEmailJob every minute"));
});

// Add Quartz hosted service
builder.Services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

// Build and run
var host = builder.Build();

return await host.RunMilvaionWorkerAsync();
