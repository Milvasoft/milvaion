namespace Milvaion.Application.Interfaces.Redis;

/// <summary>
/// Redis Pub/Sub service for job cancellation signals.
/// </summary>
public interface IRedisCancellationService
{
    /// <summary>
    /// Publishes a cancellation signal for a job.
    /// </summary>
    /// <param name="jobId">Job identifier to cancel</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of subscribers that received the message</returns>
    Task<long> PublishCancellationAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes to job cancellation signals.
    /// </summary>
    /// <param name="onCancellation">Callback invoked when a cancellation signal is received</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SubscribeToCancellationsAsync(Action<Guid> onCancellation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Unsubscribes from job cancellation signals.
    /// </summary>
    Task UnsubscribeFromCancellationsAsync();
}
