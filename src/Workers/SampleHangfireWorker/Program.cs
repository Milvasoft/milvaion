using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Milvasoft.Milvaion.Sdk.Worker;
using Milvasoft.Milvaion.Sdk.Worker.Hangfire.Extensions;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using SampleHangfireWorker.Jobs;
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

// Add Milvaion Hangfire integration
// This registers core worker services (heartbeat, status updates, etc.)
// AND Hangfire-specific filters for job monitoring
builder.Services.AddMilvaionHangfireIntegration(builder.Configuration);

// Register job classes for DI
builder.Services.AddTransient<SampleLogJob>();
builder.Services.AddTransient<SendEmailJob>();

// Configure Hangfire with InMemory storage (for demo purposes)
builder.Services.AddHangfire((sp, config) =>
{
    config.UseInMemoryStorage();

    // Use Milvaion filter for job monitoring
    config.UseMilvaion(sp);
});

// Add Hangfire server
builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = 4;
    options.Queues = ["default", "critical"];
});

// Build and run
var host = builder.Build();

// Configure recurring jobs after host is built
using (var scope = host.Services.CreateScope())
{
    var recurringJobManager = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();

    // Register SampleLogJob - runs every 30 seconds
    // PerformContext is automatically injected by Hangfire, pass null in expression
    recurringJobManager.AddOrUpdate<SampleLogJob>("sample-log-job", job => job.ExecuteAsync(null, CancellationToken.None), "*/30 * * * * *");

    // Register SendEmailJob - runs every 10 seconds
    // PerformContext is automatically injected by Hangfire, pass null in expression
    recurringJobManager.AddOrUpdate<SendEmailJob>("send-email-job", job => job.ExecuteAsync(null, "test@example.com", "Scheduled Notification", CancellationToken.None), "*/10 * * * * *");
}

return await host.RunMilvaionWorkerAsync();
