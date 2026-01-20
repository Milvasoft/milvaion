namespace Milvasoft.Milvaion.Sdk.Worker.Exceptions;

/// <summary>
/// Exception indicating a permanent job failure that should NOT be retried.
/// When this exception is thrown, the job will be immediately moved to the Dead Letter Queue (DLQ).
/// </summary>
/// <remarks>
/// Use this for errors that won't be fixed by retrying:
/// <list type="bullet">
/// <item>Invalid job data (JSON parsing errors)</item>
/// <item>Missing required fields</item>
/// <item>Business rule violations</item>
/// <item>Authentication failures (401/403)</item>
/// <item>Resource not found (404)</item>
/// </list>
/// 
/// For transient errors (network timeouts, rate limits, service unavailable), 
/// throw a regular exception and let the retry mechanism handle it.
/// </remarks>
/// <example>
/// <code>
/// public async Task ExecuteAsync(IJobContext context)
/// {
///     EmailJobData data;
///     
///     try
///     {
///         data = JsonSerializer.Deserialize&lt;EmailJobData&gt;(context.Job.JobData ?? "{}");
///     }
///     catch (JsonException ex)
///     {
///         // Permanent error - will go directly to DLQ
///         throw new PermanentJobException("Invalid job data format", ex);
///     }
///     
///     if (string.IsNullOrWhiteSpace(data?.To))
///     {
///         throw new PermanentJobException("Recipient email is required");
///     }
///     
///     // Continue...
/// }
/// </code>
/// </example>
public class PermanentJobException : Exception
{
    /// <summary>
    /// Creates a new instance of <see cref="PermanentJobException"/>.
    /// </summary>
    public PermanentJobException()
    {
    }

    /// <summary>
    /// Creates a new instance of <see cref="PermanentJobException"/> with a message.
    /// </summary>
    /// <param name="message">Error message describing why the job permanently failed.</param>
    public PermanentJobException(string message) : base(message)
    {
    }

    /// <summary>
    /// Creates a new instance of <see cref="PermanentJobException"/> with a message and inner exception.
    /// </summary>
    /// <param name="message">Error message describing why the job permanently failed.</param>
    /// <param name="innerException">The original exception that caused this failure.</param>
    public PermanentJobException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
