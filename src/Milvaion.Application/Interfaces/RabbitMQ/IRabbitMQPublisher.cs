namespace Milvaion.Application.Interfaces.RabbitMQ;

/// <summary>
/// RabbitMQ publisher for dispatching jobs to workers.
/// </summary>
public interface IRabbitMQPublisher
{
    /// <summary>
    /// Publishes a scheduled job to the RabbitMQ queue.
    /// </summary>
    /// <param name="job">The scheduled job to publish</param>
    /// <param name="occurrenceId">Unique identifier for the job occurrence (Occurrence.Id)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if published successfully</returns>
    /// <remarks>
    /// <c>{{secret:name}}</c> references in the job data are resolved into the message only; the job object is
    /// not changed.
    /// </remarks>
    /// <exception cref="Secrets.SecretResolutionException">
    /// A referenced secret could not be resolved. Nothing was published; fail the occurrence with the
    /// exception message (it names the secrets, never their values) rather than retrying.
    /// </exception>
    Task<bool> PublishJobAsync(ScheduledJob job, Guid occurrenceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes multiple jobs in a batch with occurrence IDs.
    /// </summary>
    /// <param name="jobsWithOccurrence">Dictionary of jobs with their occurrence IDs</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of jobs published successfully</returns>
    Task<int> PublishBatchAsync(Dictionary<ScheduledJob, Guid> jobsWithOccurrence, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the number of messages in a specific RabbitMQ queue for a job.
    /// Used to check if there are queued occurrences before dispatching Skip policy jobs.
    /// </summary>
    /// <param name="routingPattern">Worker routing patterns (e.g., ["nonparallel.*"]). If null, uses default "all" queue.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of messages in queue, or 0 if queue doesn't exist</returns>
    Task<uint> GetQueueMessageCountAsync(string routingPattern, CancellationToken cancellationToken = default);
}
