namespace Milvaion.Application.Dtos.ConfigurationDtos;

/// <summary>
/// Job dispatcher configuration.
/// </summary>
public class JobDispatcherConfigDto
{
    /// <summary>
    /// Whether the dispatcher is enabled.
    /// Default: true.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Polling interval in seconds (how often to check Redis for due jobs).
    /// Default: 10 seconds.
    /// </summary>
    public int PollingIntervalSeconds { get; set; }

    /// <summary>
    /// Maximum number of jobs to retrieve from Redis in one batch.
    /// Default: 100.
    /// </summary>
    public int BatchSize { get; set; }

    /// <summary>
    /// Lock TTL in seconds when checking if a job is already locked.
    /// Default: 600 seconds (10 minutes).
    /// </summary>
    public int LockTtlSeconds { get; set; }

    /// <summary>
    /// Whether to perform zombie job recovery on startup.
    /// Default: true.
    /// </summary>
    public bool EnableStartupRecovery { get; set; }
}