using Milvasoft.Milvaion.Sdk.Worker.Abstractions;

namespace ApiWorker.Jobs;

/// <summary>
/// Simple test job that logs and waits.
/// </summary>
public class SimpleJob : IAsyncJob
{
    public async Task ExecuteAsync(IJobContext context)
    {
        context.LogInformation("🚀 SimpleJob started!");
        context.LogInformation($"Job ID: {context.Job.Id}");
        context.LogInformation($"Job Type: {context.Job.JobNameInWorker}");
        context.LogInformation($"CorrelationId: {context.CorrelationId}");
        context.LogInformation($"WorkerId: {context.WorkerId}");

        // Simulate work
        for (int i = 1; i <= 5; i++)
        {
            context.LogInformation($"⏳ Processing step {i}/5...");
            await Task.Delay(500, context.CancellationToken);
        }

        context.LogInformation("✅ SimpleJob completed successfully!");
    }
}

/// <summary>
/// Example email sending job.
/// </summary>
public class SendEmailJob : IAsyncJob<EmailJobData>
{
    public async Task ExecuteAsync(IJobContext context)
    {
        context.LogInformation("📧 SendEmailJob started!");

        // Parse job data
        var jobData = context.GetData<EmailJobData>();

        context.LogInformation($"Sending email to: {jobData?.To ?? "unknown"}");
        context.LogInformation($"Subject: {jobData?.Subject ?? "No subject"}");

        // Simulate email sending with progress updates
        for (int i = 0; i <= 100; i += 20)
        {
            context.LogInformation($"Sending progress: {i}%");
            await Task.Delay(1000, context.CancellationToken);
        }

        context.LogInformation($"✅ Email sent successfully to {jobData?.To}");
    }
}

public class EmailJobData
{
    public string To { get; set; }
    public string Subject { get; set; }
    public string Body { get; set; }
}