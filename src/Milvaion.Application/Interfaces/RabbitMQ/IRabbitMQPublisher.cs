namespace Milvaion.Application.Interfaces.RabbitMQ;

/// <summary>
/// RabbitMQ publisher for dispatching jobs to workers.
/// </summary>
public interface IRabbitMQPublisher
{
    /// <summary>
    /// Publishes a scheduled job to the RabbitMQ queue with correlation tracking.
    /// </summary>
    /// <param name="job">The scheduled job to publish</param>
    /// <param name="correlationId">Correlation ID for distributed tracing</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if published successfully</returns>
    Task<bool> PublishJobAsync(ScheduledJob job, Guid correlationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes multiple jobs in a batch with correlation IDs.
    /// </summary>
    /// <param name="jobsWithCorrelation">Dictionary of jobs with their correlation IDs</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of jobs published successfully</returns>
    Task<int> PublishBatchAsync(Dictionary<ScheduledJob, Guid> jobsWithCorrelation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the number of messages in a specific RabbitMQ queue for a job.
    /// Used to check if there are queued occurrences before dispatching Skip policy jobs.
    /// </summary>
    /// <param name="routingPattern">Worker routing patterns (e.g., ["nonparallel.*"]). If null, uses default "all" queue.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of messages in queue, or 0 if queue doesn't exist</returns>
    Task<uint> GetQueueMessageCountAsync(string routingPattern, CancellationToken cancellationToken = default);
}
