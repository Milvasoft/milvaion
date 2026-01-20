namespace Milvasoft.Milvaion.Sdk.Worker.Options;

/// <summary>
/// Configuration for job-specific consumers.
/// Maps directly to JobConsumers section in appsettings.json.
/// Each key in JobConsumers becomes a dictionary entry with job name as key.
/// </summary>
public class JobConsumerOptions : Dictionary<string, JobConsumerConfig>
{
    public const string SectionKey = "JobConsumers";
}

/// <summary>
/// Individual job consumer configuration.
/// </summary>
public class JobConsumerConfig
{
    /// <summary>
    /// Consumer identifier (user-defined, e.g., "test-consumer").
    /// Used to identify this specific job consumer.
    /// </summary>
    public string ConsumerId { get; set; }

    /// <summary>
    /// Routing patterns this consumer handles.
    /// </summary>
    public string RoutingPattern { get; set; }

    /// <summary>
    /// Maximum parallel jobs this consumer can run simultaneously. RabbitMQ prefetch count.
    /// </summary>
    public int MaxParallelJobs { get; set; } = 10;

    /// <summary>
    /// Maximum execution time allowed for jobs in this consumer.
    /// If a job exceeds this timeout, it will be cancelled and marked as TimedOut.
    /// Default: 1 hour (3600 seconds).
    /// Set to 0 or negative value for no timeout (not recommended).
    /// </summary>
    public int ExecutionTimeoutSeconds { get; set; } = 3600; // 1 hour default

    /// <summary>
    /// Maximum number of retry attempts before moving to DLQ.
    /// Default: 3 retries.
    /// Set to 0 to disable retries (immediate DLQ on failure).
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Base delay in seconds for exponential backoff retry strategy.
    /// Actual delay = BaseRetryDelaySeconds * (2 ^ retryAttempt).
    /// Example: BaseRetryDelaySeconds=5 ? 5s, 10s, 20s, 40s...
    /// Default: 5 seconds.
    /// </summary>
    public int BaseRetryDelaySeconds { get; set; } = 5;

    /// <summary>
    /// Determine whether user-friendly logs should be logged via configured IMilvaLogger(ILogger).
    /// </summary>
    public bool LogUserFriendlyLogsViaLogger { get; set; }

    /// <summary>
    /// The job implementation type. Set automatically during auto-discovery.
    /// Used to extract job data type information.
    /// </summary>
    public Type JobType { get; set; }
}
