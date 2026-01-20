namespace Milvasoft.Milvaion.Sdk.Worker.Options;

public class OfflineResilienceSettings
{
    /// <summary>
    /// Enable offline resilience (local state persistence).
    /// When enabled, status updates and logs are stored locally first,
    /// then synced to scheduler when connection is available.
    /// Default: true
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Path to local SQLite database for state persistence.
    /// Default: "./worker_data"
    /// </summary>
    public string LocalStoragePath { get; set; } = "./worker_data";

    /// <summary>
    /// Interval (in seconds) for syncing pending items to scheduler.
    /// Default: 30 seconds
    /// </summary>
    public int SyncIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum number of retry attempts for failed sync operations.
    /// After max retries, items are marked as synced to prevent blocking.
    /// Default: 3
    /// </summary>
    public int MaxSyncRetries { get; set; } = 3;

    /// <summary>
    /// Interval (in hours) for cleaning up old synced records.
    /// Default: 6 hours
    /// </summary>
    public int CleanupIntervalHours { get; set; } = 1;

    /// <summary>
    /// Retention period (in days) for synced records before cleanup.
    /// Default: 7 days
    /// </summary>
    public int RecordRetentionDays { get; set; } = 1;
}
