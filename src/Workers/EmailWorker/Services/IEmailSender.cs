using EmailWorker.Jobs;

namespace EmailWorker.Services;

/// <summary>
/// Interface for email sending implementations.
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// Sends an email using the specified configuration.
    /// </summary>
    /// <param name="configName">SMTP configuration name (null for default)</param>
    /// <param name="emailData">Email data to send</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing message ID or error details</returns>
    Task<EmailSendResult> SendEmailAsync(string configName, EmailJobData emailData, CancellationToken cancellationToken);

    /// <summary>
    /// Checks if the specified SMTP configuration exists.
    /// </summary>
    bool ConfigurationExists(string configName);

    /// <summary>
    /// Gets list of available SMTP configuration names.
    /// </summary>
    IReadOnlyList<string> GetAvailableConfigNames();
}

/// <summary>
/// Result of an email send operation.
/// </summary>
public class EmailSendResult
{
    /// <summary>
    /// Whether the email was sent successfully.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Server-assigned message ID (if available).
    /// </summary>
    public string MessageId { get; set; }

    /// <summary>
    /// Error message if sending failed.
    /// </summary>
    public string ErrorMessage { get; set; }

    /// <summary>
    /// Number of recipients the email was sent to.
    /// </summary>
    public int RecipientCount { get; set; }

    /// <summary>
    /// Time taken to send the email in milliseconds.
    /// </summary>
    public long DurationMs { get; set; }

    /// <summary>
    /// Creates a success result.
    /// </summary>
    public static EmailSendResult Succeeded(string messageId, int recipientCount, long durationMs) => new()
    {
        Success = true,
        MessageId = messageId,
        RecipientCount = recipientCount,
        DurationMs = durationMs
    };

    /// <summary>
    /// Creates a failure result.
    /// </summary>
    public static EmailSendResult Failed(string errorMessage) => new()
    {
        Success = false,
        ErrorMessage = errorMessage
    };
}
