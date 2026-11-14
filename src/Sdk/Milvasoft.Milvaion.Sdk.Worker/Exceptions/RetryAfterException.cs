namespace Milvasoft.Milvaion.Sdk.Worker.Exceptions;

/// <summary>
/// Thrown by a job to ask for another run at a specific time, instead of an immediate retry with backoff.
/// Use it when the reason the job cannot run now has a known end, such as an API quota that resets at 11:00 UTC.
/// </summary>
/// <remarks>
/// <para>
/// The worker does not retry the job itself and does not move it to the dead letter queue. It reports the occurrence as
/// Failed together with <see cref="RetryAt"/>.
/// </para>
/// <para>
/// In a workflow step, Milvaion reruns that step at <see cref="RetryAt"/>. These reruns don't count against the workflow's
/// MaxStepRetries; the workflow's MaxRetryAfter setting (default 10) caps them per step and run. In a standalone job the
/// occurrence simply ends as Failed.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// if (response.StatusCode == HttpStatusCode.TooManyRequests)
///     throw new RetryAfterException(DateTime.UtcNow.Date.AddHours(11), "YouTube upload quota used up");
/// </code>
/// </example>
public class RetryAfterException : Exception
{
    /// <summary>
    /// Time (UTC) the job asks to be run again at.
    /// </summary>
    public DateTime RetryAt { get; }

    /// <summary>
    /// Creates a new instance of <see cref="RetryAfterException"/>.
    /// </summary>
    /// <param name="retryAt">Time to run again at. Converted to UTC; an unspecified kind is treated as UTC.</param>
    /// <param name="message">Why the job cannot run now.</param>
    public RetryAfterException(DateTime retryAt, string message = null) : this(retryAt, message, null)
    {
    }

    /// <summary>
    /// Creates a new instance of <see cref="RetryAfterException"/> with an inner exception.
    /// </summary>
    /// <param name="retryAt">Time to run again at. Converted to UTC; an unspecified kind is treated as UTC.</param>
    /// <param name="message">Why the job cannot run now.</param>
    /// <param name="innerException">The original exception.</param>
    public RetryAfterException(DateTime retryAt, string message, Exception innerException)
        : base(message ?? $"Job asked to be retried at {ToUtc(retryAt):O}.", innerException)
    {
        RetryAt = ToUtc(retryAt);
    }

    /// <summary>
    /// Creates a new instance of <see cref="RetryAfterException"/> from a point in time with an offset.
    /// </summary>
    /// <param name="retryAt">Time to run again at.</param>
    /// <param name="message">Why the job cannot run now.</param>
    public RetryAfterException(DateTimeOffset retryAt, string message = null) : this(retryAt.UtcDateTime, message, null)
    {
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
