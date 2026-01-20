using EmailWorker.Services;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Milvasoft.Milvaion.Sdk.Worker.Exceptions;
using System.Text.Json;

namespace EmailWorker.Jobs;

/// <summary>
/// Generic email sending job that supports SMTP with configurable providers.
/// Supports HTML/text emails, attachments, CC/BCC, and multiple SMTP configurations.
/// </summary>
public class SendEmailJob(IEmailSender emailSender) : IAsyncJobWithResult<EmailJobData>
{
    private readonly IEmailSender _emailSender = emailSender;

    public async Task<string> ExecuteAsync(IJobContext context)
    {
        // 1. Get typed job data
        var jobData = context.GetData<EmailJobData>() ?? throw new PermanentJobException("EmailJobData is required but was null");

        // 2. Validate required fields
        if (jobData.To == null || jobData.To.Count == 0)
            throw new PermanentJobException("At least one recipient email address is required (To)");

        if (string.IsNullOrWhiteSpace(jobData.Subject))
            throw new PermanentJobException("Email subject is required");

        if (string.IsNullOrWhiteSpace(jobData.Body))
            throw new PermanentJobException("Email body is required");

        // 3. Validate configuration exists
        if (!string.IsNullOrEmpty(jobData.ConfigName) && !_emailSender.ConfigurationExists(jobData.ConfigName))
        {
            var available = string.Join(", ", _emailSender.GetAvailableConfigNames());
            throw new PermanentJobException($"SMTP configuration '{jobData.ConfigName}' not found. Available: {available}");
        }

        // 4. Log email details
        var logData = new Dictionary<string, object>
        {
            { "ConfigName", jobData.ConfigName ?? "Default" },
            { "To", jobData.To },
            { "Cc", jobData.Cc },
            { "BccCount", jobData.Bcc?.Count ?? 0 },
            { "Subject", jobData.Subject },
            { "IsHtml", jobData.IsHtml },
            { "AttachmentCount", jobData.Attachments?.Count ?? 0 }
        };

        context.LogInformation($"📧 Sending email via '{jobData.ConfigName ?? "Default"}' configuration", logData);

        // 5. Send the email
        var result = await _emailSender.SendEmailAsync(jobData.ConfigName, jobData, context.CancellationToken);

        // 6. Handle result
        if (!result.Success)
        {
            // Check if it's a transient error (retry) or permanent error (no retry)
            if (IsTransientError(result.ErrorMessage))
            {
                context.LogWarning($"Transient email error (will retry): {result.ErrorMessage}");
                throw new Exception(result.ErrorMessage); // Will trigger retry
            }
            else
            {
                context.LogError($"Permanent email error: {result.ErrorMessage}");
                throw new PermanentJobException(result.ErrorMessage); // No retry
            }
        }

        context.LogInformation($"✅ Email sent successfully!");

        // 7. Return result JSON
        return JsonSerializer.Serialize(new
        {
            Success = true,
            result.MessageId,
            result.RecipientCount,
            result.DurationMs,
            jobData.To,
            jobData.Subject
        });
    }

    /// <summary>
    /// Determines if an error is transient (should be retried) or permanent.
    /// </summary>
    private static bool IsTransientError(string errorMessage)
    {
        if (string.IsNullOrEmpty(errorMessage))
            return false;

        var message = errorMessage.ToLowerInvariant();

        // Transient errors - worth retrying
        return message.Contains("timeout") ||
               message.Contains("connection") ||
               message.Contains("network") ||
               message.Contains("temporarily") ||
               message.Contains("try again") ||
               message.Contains("service unavailable") ||
               message.Contains("too many connections") ||
               message.Contains("rate limit");
    }
}