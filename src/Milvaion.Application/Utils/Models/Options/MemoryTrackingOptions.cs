namespace Milvaion.Application.Utils.Models.Options;

/// <summary>
/// Configuration options for memory tracking.
/// </summary>
public class MemoryTrackingOptions
{
    /// <summary>
    /// Minimum seconds between memory checks (default: 10s).
    /// </summary>
    public int CheckIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Log memory stats every N iterations (default: 50).
    /// </summary>
    public int LogIntervalIterations { get; set; } = 50;

    /// <summary>
    /// Warning threshold in bytes (default: 100 MB).
    /// </summary>
    public long WarningThresholdBytes { get; set; } = 100 * 1024 * 1024;

    /// <summary>
    /// Critical threshold that triggers forced GC (default: 500 MB).
    /// </summary>
    public long CriticalThresholdBytes { get; set; } = 500 * 1024 * 1024;

    /// <summary>
    /// Total growth threshold for leak detection (default: 1 GB).
    /// </summary>
    public long LeakDetectionThresholdBytes { get; set; } = 1024 * 1024 * 1024;

    /// <summary>
    /// Minimum iterations before checking for leaks (default: 100).
    /// </summary>
    public int LeakDetectionMinIterations { get; set; } = 100;
}
