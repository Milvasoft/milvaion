namespace Milvasoft.Milvaion.Sdk.Domain.Enums;

/// <summary>
/// Represents the execution status of a job occurrence.
/// </summary>
public enum JobOccurrenceStatus
{
    /// <summary>
    /// Job has been queued in RabbitMQ, waiting for worker pickup.
    /// </summary>
    Queued,

    /// <summary>
    /// Worker is currently executing the job.
    /// </summary>
    Running,

    /// <summary>
    /// Job execution completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// Job execution failed with an exception.
    /// </summary>
    Failed,

    /// <summary>
    /// Job execution was cancelled (via Redis Pub/Sub signal).
    /// </summary>
    Cancelled,

    /// <summary>
    /// Job execution timed out.
    /// </summary>
    TimedOut,

    /// <summary>
    /// Job status unknown - lost heartbeat from worker.
    /// Possible causes: Worker crashed, RabbitMQ connection lost, or network failure.
    /// Health monitor marks running jobs as Unknown when they don't send heartbeat for threshold time.
    /// </summary>
    Unknown,
}
