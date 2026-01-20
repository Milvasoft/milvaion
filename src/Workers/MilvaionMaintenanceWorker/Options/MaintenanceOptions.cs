namespace MilvaionMaintenanceWorker.Options;

/// <summary>
/// Configuration options for maintenance jobs.
/// </summary>
public class MaintenanceOptions
{
    public const string SectionKey = "MaintenanceConfig";

    /// <summary>
    /// Database connection string for maintenance operations.
    /// </summary>
    public string DatabaseConnectionString { get; set; }

    /// <summary>
    /// Redis connection string for cache cleanup operations.
    /// </summary>
    public string RedisConnectionString { get; set; }

    /// <summary>
    /// Occurrence retention settings.
    /// </summary>
    public OccurrenceRetentionSettings OccurrenceRetention { get; set; } = new();

    /// <summary>
    /// Occurrence archive settings.
    /// </summary>
    public OccurrenceArchiveSettings OccurrenceArchive { get; set; } = new();

    /// <summary>
    /// Failed occurrence retention settings.
    /// </summary>
    public FailedOccurrenceRetentionSettings FailedOccurrenceRetention { get; set; } = new();

    /// <summary>
    /// Database maintenance settings.
    /// </summary>
    public DatabaseMaintenanceSettings DatabaseMaintenance { get; set; } = new();

    /// <summary>
    /// Redis cleanup settings.
    /// </summary>
    public RedisCleanupSettings RedisCleanup { get; set; } = new();

    /// <summary>
    /// Activity log retention settings.
    /// </summary>
    public ActivityLogRetentionSettings ActivityLogRetention { get; set; } = new();

    /// <summary>
    /// Notification retention settings.
    /// </summary>
    public NotificationRetentionSettings NotificationRetention { get; set; } = new();
}

/// <summary>
/// Occurrence retention configuration.
/// </summary>
public class OccurrenceRetentionSettings
{
    /// <summary>
    /// Days to keep completed (success) occurrences.
    /// </summary>
    public int CompletedRetentionDays { get; set; } = 30;

    /// <summary>
    /// Days to keep failed occurrences.
    /// </summary>
    public int FailedRetentionDays { get; set; } = 90;

    /// <summary>
    /// Days to keep cancelled occurrences.
    /// </summary>
    public int CancelledRetentionDays { get; set; } = 30;

    /// <summary>
    /// Days to keep timed out occurrences.
    /// </summary>
    public int TimedOutRetentionDays { get; set; } = 60;

    /// <summary>
    /// Batch size for deletion to avoid long locks.
    /// </summary>
    public int BatchSize { get; set; } = 1000;
}

/// <summary>
/// Failed occurrence (DLQ) retention configuration.
/// </summary>
public class FailedOccurrenceRetentionSettings
{
    /// <summary>
    /// Days to keep failed occurrences in DLQ.
    /// </summary>
    public int RetentionDays { get; set; } = 180;

    /// <summary>
    /// Batch size for deletion.
    /// </summary>
    public int BatchSize { get; set; } = 500;
}

/// <summary>
/// Database maintenance configuration.
/// </summary>
public class DatabaseMaintenanceSettings
{
    /// <summary>
    /// Whether to run VACUUM on tables.
    /// </summary>
    public bool EnableVacuum { get; set; } = true;

    /// <summary>
    /// Whether to run ANALYZE on tables.
    /// </summary>
    public bool EnableAnalyze { get; set; } = true;

    /// <summary>
    /// Whether to run REINDEX on tables.
    /// </summary>
    public bool EnableReindex { get; set; } = false;

    /// <summary>
    /// Tables to maintain. Must be specified in config.
    /// Example: ["JobOccurrences", "ScheduledJobs", "FailedOccurrences"]
    /// </summary>
    public List<string> Tables { get; set; }
}

/// <summary>
/// Redis cleanup configuration.
/// </summary>
public class RedisCleanupSettings
{
    /// <summary>
    /// Key prefix for scheduler-related Redis keys.
    /// </summary>
    public string KeyPrefix { get; set; } = "Milvaion:JobScheduler:";

    /// <summary>
    /// Whether to clean orphaned job cache entries.
    /// </summary>
    public bool CleanOrphanedJobCache { get; set; } = true;

    /// <summary>
    /// Whether to clean stale lock entries.
    /// </summary>
    public bool CleanStaleLocks { get; set; } = true;

    /// <summary>
    /// Whether to clean orphaned running job states.
    /// </summary>
    public bool CleanOrphanedRunningStates { get; set; } = true;

    /// <summary>
    /// Maximum age in hours for lock entries before considered stale.
    /// </summary>
    public int StaleLockHours { get; set; } = 24;
}

/// <summary>
/// Occurrence archive configuration.
/// Archives old occurrences to dated tables instead of deleting them.
/// </summary>
public class OccurrenceArchiveSettings
{
    /// <summary>
    /// Number of days after which occurrences should be archived.
    /// </summary>
    public int ArchiveAfterDays { get; set; } = 90;

    /// <summary>
    /// Prefix for archive table names.
    /// Final name will be: {Prefix}_{Year}_{Month} (e.g., JobOccurrences_Archive_2024_01)
    /// </summary>
    public string ArchiveTablePrefix { get; set; } = "JobOccurrences_Archive";

    /// <summary>
    /// Status values to archive.
    /// Default: Completed (2), Failed (3), Cancelled (4), TimedOut (5)
    /// </summary>
    public List<int> StatusesToArchive { get; set; }

    /// <summary>
    /// Batch size for archiving to avoid long locks.
    /// </summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>
    /// Whether to create indexes on the archive table.
    /// </summary>
    public bool CreateIndexOnArchive { get; set; } = true;
}

/// <summary>
/// Activity log retention configuration.
/// </summary>
public class ActivityLogRetentionSettings
{
    /// <summary>
    /// Days to keep activity logs.
    /// </summary>
    public int RetentionDays { get; set; } = 60;
}

/// <summary>
/// Notification retention configuration.
/// </summary>
public class NotificationRetentionSettings
{
    /// <summary>
    /// Days to keep seen notifications.
    /// </summary>
    public int SeenRetentionDays { get; set; } = 30;

    /// <summary>
    /// Days to keep unseen notifications.
    /// </summary>
    public int UnseenRetentionDays { get; set; } = 60;
}

