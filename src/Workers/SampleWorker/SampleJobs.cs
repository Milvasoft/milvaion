using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace SampleWorker;

/// <summary>
/// Simple test job that logs and waits.
/// This job doesn't require any job data.
/// </summary>
public class TestJob : IAsyncJob
{
    public async Task ExecuteAsync(IJobContext context)
    {
        context.LogInformation("TestJob started!");
        context.LogInformation($"Job ID: {context.Job.Id}");
        context.LogInformation($"Job Type: {context.Job.JobNameInWorker}");
        context.LogInformation($"CorrelationId: {context.CorrelationId}");
        context.LogInformation($"WorkerId: {context.WorkerId}");

        // Simulate work
        for (var i = 1; i <= 5; i++)
        {
            context.LogInformation($"Processing step {i}/5...");
            await Task.Delay(500, context.CancellationToken);
        }

        context.LogInformation("TestJob completed successfully!");
    }
}

/// <summary>
/// Example email sending job with typed job data.
/// Uses IAsyncJob&lt;EmailJobData&gt; to define expected data schema.
/// </summary>
public class SampleSendEmailJob : IAsyncJob<EmailJobData>
{
    public async Task ExecuteAsync(IJobContext context)
    {
        context.LogInformation("SendEmailJob started!");

        // Type-safe job data access
        var jobData = context.GetData<EmailJobData>();

        context.LogInformation($"Sending email to: {jobData?.To ?? "unknown"}");
        context.LogInformation($"Subject: {jobData?.Subject ?? "No subject"}");

        for (var i = 0; i < 30000; i += 1000)
        {
            context.LogInformation($"New operation process {i}");

            // Simulate email sending
            await Task.Delay(1000, context.CancellationToken);
        }

        context.LogInformation($"Email sent successfully to {jobData?.To}");
    }
}

/// <summary>
/// Long running test job for timeout testing.
/// </summary>
public class LongRunningTestJob : IAsyncJob
{
    public async Task ExecuteAsync(IJobContext context)
    {
        context.LogInformation("Starting long running task...");

        await Task.Delay(TimeSpan.FromSeconds(20), context.CancellationToken);

        context.LogInformation("Task completed!");
    }
}

/// <summary>
/// Non-parallel job for testing sequential execution.
/// </summary>
public class NonParallelJob : IAsyncJob
{
    public async Task ExecuteAsync(IJobContext context)
    {
        context.LogInformation("Non parallel task...");

        await Task.Delay(TimeSpan.FromSeconds(10), context.CancellationToken);

        context.LogInformation("Task completed!");
    }
}

/// <summary>
/// Always failing job for DLQ testing.
/// </summary>
public class AlwaysFailingJob : IAsyncJob
{
    public Task ExecuteAsync(IJobContext context)
    {
        context.LogInformation("Always failing task...");

        throw new InvalidOperationException("This job always fails for DLQ testing!");
    }
}

/// <summary>
/// Email job data definition.
/// This schema will be automatically discovered and sent to the scheduler.
/// </summary>
public class EmailJobData
{
    /// <summary>
    /// Recipient email address.
    /// </summary>
    [Required]
    [Description("The email address to send to")]
    public string To { get; set; }

    /// <summary>
    /// Email subject line.
    /// </summary>
    [Required]
    [Description("The subject of the email")]
    public string Subject { get; set; }

    /// <summary>
    /// Email body content. Can be plain text or HTML.
    /// </summary>
    [Description("The body content of the email")]
    public string Body { get; set; }

    /// <summary>
    /// CC recipients (optional).
    /// </summary>
    [Description("Carbon copy recipients")]
    public List<string> Cc { get; set; }

    /// <summary>
    /// BCC recipients (optional).
    /// </summary>
    [Description("Blind carbon copy recipients")]
    public List<string> Bcc { get; set; }

    /// <summary>
    /// Email priority.
    /// </summary>
    [DefaultValue(EmailPriority.Normal)]
    [Description("The priority level of the email")]
    public EmailPriority Priority { get; set; } = EmailPriority.Normal;

    /// <summary>
    /// Whether the body is HTML.
    /// </summary>
    [DefaultValue(false)]
    [Description("Set to true if the body contains HTML")]
    public bool IsHtml { get; set; }
}

/// <summary>
/// Email priority levels.
/// </summary>
public enum EmailPriority
{
    Low,
    Normal,
    High
}