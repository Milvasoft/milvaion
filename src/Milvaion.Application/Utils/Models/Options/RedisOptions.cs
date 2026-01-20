namespace Milvaion.Application.Utils.Models.Options;

/// <summary>
/// Redis configuration options for job scheduler.
/// </summary>
public class RedisOptions
{
    /// <summary>
    /// Configuration section key.
    /// </summary>
    public const string SectionKey = "MilvaionConfig:Redis";

    /// <summary>
    /// Redis connection string (e.g., "localhost:6379").
    /// </summary>
    public string ConnectionString { get; set; } = "localhost:6379";

    /// <summary>
    /// Optional password for Redis authentication.
    /// </summary>
    public string Password { get; set; }

    /// <summary>
    /// Redis database number (0-15).
    /// </summary>
    public int Database { get; set; } = 0;

    /// <summary>
    /// Connection timeout in milliseconds.
    /// </summary>
    public int ConnectTimeout { get; set; } = 5000;

    /// <summary>
    /// Sync timeout for Redis operations in milliseconds.
    /// </summary>
    public int SyncTimeout { get; set; } = 5000;

    /// <summary>
    /// Key prefix for job scheduler keys (e.g., "Milvaion:JobScheduler:").
    /// </summary>
    public string KeyPrefix { get; set; } = "Milvaion:JobScheduler:";

    /// <summary>
    /// ZSET key name for scheduled jobs.
    /// </summary>
    public string ScheduledJobsKey => $"{KeyPrefix}scheduled_jobs";

    /// <summary>
    /// Key pattern for job locks: {KeyPrefix}lock:{jobId}
    /// </summary>
    public string GetLockKey(Guid jobId) => $"{KeyPrefix}lock:{jobId}";

    /// <summary>
    /// Pub/Sub channel name for job cancellation signals.
    /// </summary>
    public string CancellationChannel => $"{KeyPrefix}cancellation_channel";

    /// <summary>
    /// Default lock TTL in seconds (10 minutes).
    /// </summary>
    public int DefaultLockTtlSeconds { get; set; } = 600;
}
