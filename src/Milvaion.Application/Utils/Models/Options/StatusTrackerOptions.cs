namespace Milvaion.Application.Utils.Models.Options;

/// <summary>
/// Configuration options for LogCollectorService.
/// </summary>
public class StatusTrackerOptions : BackgrounServiceOptions
{
    /// <summary>
    /// Section key in configuration files.
    /// </summary>
    public const string SectionKey = "MilvaionConfig:StatusTracker";

    /// <summary>
    /// Batch size for processing status updates.
    /// </summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// Timeout (in milliseconds) for batching status updates.
    /// </summary>
    public int BatchIntervalMs { get; set; } = 500;

    /// <summary>
    /// Maximum number of execution log entries to keep per job occurrence.
    /// </summary>
    public int ExecutionLogMaxCount { get; set; } = 100;
}
