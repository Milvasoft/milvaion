namespace Milvaion.Domain.Enums;

/// <summary>
/// Defines the types of alerts that can be sent through the alerting system.
/// </summary>
public enum AlertType : byte
{
    /// <summary>
    /// Recieve all types of alerts.
    /// </summary>
    All = 0,

    /// <summary>
    /// Job dispatcher memory usage has reached critical levels.
    /// </summary>
    JobDispatcherMemoryUsageCritical = 1,

    /// <summary>
    /// Database connection failed or is unavailable.
    /// </summary>
    DatabaseConnectionFailed = 2,

    /// <summary>
    /// A zombie occurrence was detected (job stuck in running state).
    /// </summary>
    ZombieOccurrenceDetected = 3,

    /// <summary>
    /// A job was automatically disabled due to consecutive failures.
    /// </summary>
    JobAutoDisabled = 4,

    /// <summary>
    /// Queue depth has reached critical threshold.
    /// </summary>
    QueueDepthCritical = 5,

    /// <summary>
    /// A worker has disconnected unexpectedly.
    /// </summary>
    WorkerDisconnected = 6,

    /// <summary>
    /// Redis connection failed or is unavailable.
    /// </summary>
    RedisConnectionFailed = 7,

    /// <summary>
    /// RabbitMQ connection failed or is unavailable.
    /// </summary>
    RabbitMQConnectionFailed = 8,

    /// <summary>
    /// A scheduled job execution failed.
    /// </summary>
    JobExecutionFailed = 9,

    /// <summary>
    /// An unknown or unhandled exception occurred.
    /// </summary>
    UnknownException = 10,

    /// <summary>
    /// A failed occurrence was received from the Dead Letter Queue (max retries exceeded).
    /// </summary>
    FailedOccurrenceReceived = 11,

    /// <summary>
    /// A background service entered degraded mode or stopped after repeated failures.
    /// </summary>
    ServiceDegraded = 12,

    /// <summary>
    /// A potential memory leak was detected in a background service.
    /// </summary>
    MemoryLeakDetected = 13,

    /// <summary>
    /// An API key is approaching its expiration date.
    /// </summary>
    ApiKeyExpiring = 14,

    /// <summary>
    /// An API key has passed its expiration date.
    /// </summary>
    ApiKeyExpired = 15,

    /// <summary>
    /// A scheduled job was dispatched later than its scheduled time beyond the misfire grace
    /// window (for example the server was down when it was due).
    /// </summary>
    JobMisfired = 16,
}
