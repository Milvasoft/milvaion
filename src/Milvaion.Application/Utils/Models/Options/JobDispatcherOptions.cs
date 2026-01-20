namespace Milvaion.Application.Utils.Models.Options;

/// <summary>
/// Configuration options for the job dispatcher background service.
/// </summary>
public class JobDispatcherOptions : BackgrounServiceOptions
{
    /// <summary>
    /// Configuration section key.
    /// </summary>
    public const string SectionKey = "MilvaionConfig:JobDispatcher";

    /// <summary>
    /// Polling interval in seconds (how often to check Redis for due jobs).
    /// Default: 10 seconds.
    /// </summary>
    public int PollingIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Maximum number of jobs to retrieve from Redis in one batch.
    /// Default: 100.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Lock TTL in seconds when checking if a job is already locked.
    /// Default: 600 seconds (10 minutes).
    /// </summary>
    public int LockTtlSeconds { get; set; } = 600;

    /// <summary>
    /// Whether to perform zombie job recovery on startup.
    /// Default: true.
    /// </summary>
    public bool EnableStartupRecovery { get; set; } = true;
}
