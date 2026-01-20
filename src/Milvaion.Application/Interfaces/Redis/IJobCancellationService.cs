namespace Milvaion.Application.Interfaces.Redis;

/// <summary>
/// Service for publishing job cancellation signals.
/// </summary>
public interface IJobCancellationService
{
    /// <summary>
    /// Publishes a cancellation signal for a job occurrence.
    /// </summary>
    /// <param name="correlationId">The correlation ID of the occurrence to cancel</param>
    /// <param name="jobId">The job ID</param>
    /// <param name="occurrenceId">The occurrence ID</param>
    /// <param name="reason">Cancellation reason</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of workers that received the signal</returns>
    Task<long> PublishCancellationAsync(
        Guid correlationId,
        Guid jobId,
        Guid occurrenceId,
        string reason,
        CancellationToken cancellationToken = default);
}
