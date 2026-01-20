namespace Milvaion.Application.Utils.Models.Options;

/// <summary>
/// Configuration options for LogCollectorService.
/// </summary>
public class LogCollectorOptions : BackgrounServiceOptions
{
    /// <summary>
    /// Section key in configuration files.
    /// </summary>
    public const string SectionKey = "MilvaionConfig:LogCollector";

    /// <summary>
    /// Batch size for processing log entries.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Interval in milliseconds between processing batches.
    /// </summary>
    public int BatchIntervalMs { get; set; } = 1000;
}
