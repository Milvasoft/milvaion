namespace Milvaion.Application.Utils.Models.Options;

/// <summary>
/// Configuration options for ZombieOccurrenceDetectorService.
/// </summary>
public class ZombieOccurrenceDetectorOptions : BackgrounServiceOptions
{
    /// <summary>
    /// Section key in configuration files.
    /// </summary>
    public const string SectionKey = "MilvaionConfig:ZombieOccurrenceDetector";

    /// <summary>
    /// Interval (in seconds) between zombie detection checks.
    /// Default: 300 seconds (5 minutes)
    /// </summary>
    public int CheckIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Timeout (in minutes) before marking a Queued occurrence as zombie.
    /// Default: 10 minutes
    /// </summary>
    public int ZombieTimeoutMinutes { get; set; } = 10;
}
