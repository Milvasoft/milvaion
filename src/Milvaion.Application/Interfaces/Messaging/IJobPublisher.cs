namespace Milvaion.Application.Interfaces.Messaging;

/// <summary>
/// Dispatches jobs to workers over the configured transport (RabbitMQ or Kafka).
/// </summary>
public interface IJobPublisher
{
    /// <summary>
    /// Publishes a scheduled job to its worker.
    /// </summary>
    /// <param name="job">The scheduled job to publish</param>
    /// <param name="occurrenceId">Unique identifier for the job occurrence (Occurrence.Id)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the broker accepted the message and a worker can receive it</returns>
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
    /// Gets the number of messages waiting for a job's worker. Used to check if there are queued occurrences before
    /// dispatching Skip policy jobs. On RabbitMQ this is the depth of the job's queue; on Kafka the consumer lag of
    /// the worker's group on its job topic.
    /// </summary>
    /// <param name="routingPattern">Worker routing pattern (e.g. "nonparallel.*"). If null, uses the default "all" queue.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of waiting messages, or 0 if the queue or topic doesn't exist</returns>
    Task<uint> GetQueueMessageCountAsync(string routingPattern, CancellationToken cancellationToken = default);
}
