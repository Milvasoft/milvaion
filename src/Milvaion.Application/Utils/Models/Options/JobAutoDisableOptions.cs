namespace Milvaion.Application.Utils.Models.Options;

/// <summary>
/// Configuration options for automatic job disabling when jobs fail repeatedly.
/// This implements a circuit breaker pattern for scheduled jobs.
/// </summary>
public class JobAutoDisableOptions
{
    /// <summary>
    /// Section key in configuration files.
    /// </summary>
    public const string SectionKey = "MilvaionConfig:JobAutoDisable";

    /// <summary>
    /// Whether the auto-disable feature is globally enabled.
    /// Individual jobs can override this with their own AutoDisableEnabled setting.
    /// Default: true
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Number of consecutive failures before a job is automatically disabled.
    /// Individual jobs can override this with their own AutoDisableThreshold setting.
    /// Default: 5 consecutive failures
    /// </summary>
    public int ConsecutiveFailureThreshold { get; set; } = 5;

    /// <summary>
    /// Time window in minutes for counting consecutive failures.
    /// Failures older than this window don't count towards the threshold.
    /// This prevents jobs from being disabled due to old historical failures.
    /// Default: 60 minutes (1 hour)
    /// </summary>
    public int FailureWindowMinutes { get; set; } = 60;
}
