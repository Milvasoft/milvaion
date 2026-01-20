namespace Milvaion.Application.Dtos.ConfigurationDtos;

/// <summary>
/// Redis configuration.
/// </summary>
public class RedisConfigDto
{
    /// <summary>
    /// Redis connection string.
    /// </summary>
    public string ConnectionString { get; set; }

    /// <summary>
    /// Redis database number.
    /// </summary>
    public int Database { get; set; }

    /// <summary>
    /// Connection timeout in milliseconds.
    /// </summary>
    public int ConnectTimeout { get; set; }

    /// <summary>
    /// Sync timeout for Redis operations in milliseconds.
    /// </summary>
    public int SyncTimeout { get; set; }

    /// <summary>
    /// Key prefix for job scheduler keys (e.g., "Milvaion:JobScheduler:").
    /// </summary>
    public string KeyPrefix { get; set; }

    /// <summary>
    /// ZSET key name for scheduled jobs.
    /// </summary>
    public string ScheduledJobsKey => $"{KeyPrefix}scheduled_jobs";

    /// <summary>
    /// /Sub channel name for job cancellation signals.
    /// </summary>
    public string LockKey => $"{KeyPrefix}cancellation_channel";

    /// <summary>
    /// Pub/Sub channel name for job cancellation signals.
    /// </summary>
    public string CancellationChannel => $"{KeyPrefix}cancellation_channel";

    /// <summary>
    /// Default lock TTL in seconds (10 minutes).
    /// </summary>
    public int DefaultLockTtlSeconds { get; set; }
}