namespace Milvaion.Application.Dtos.ConfigurationDtos;

/// <summary>
/// Job dispatcher configuration.
/// </summary>
public class JobAutoDisableConfigDto
{
    /// <summary>
    /// Whether the auto-disable feature is globally enabled.
    /// Individual jobs can override this with their own AutoDisableEnabled setting.
    /// Default: true
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Number of consecutive failures before a job is automatically disabled.
    /// Individual jobs can override this with their own AutoDisableThreshold setting.
    /// Default: 5 consecutive failures
    /// </summary>
    public int ConsecutiveFailureThreshold { get; set; }

    /// <summary>
    /// Time window in minutes for counting consecutive failures.
    /// Failures older than this window don't count towards the threshold.
    /// This prevents jobs from being disabled due to old historical failures.
    /// Default: 60 minutes (1 hour)
    /// </summary>
    public int FailureWindowMinutes { get; set; }

    /// <summary>
    /// Whether to automatically re-enable jobs after a cooldown period.
    /// If true, jobs will be re-enabled after AutoReEnableCooldownMinutes.
    /// Default: false (manual re-enable required)
    /// </summary>
    public bool AutoReEnableAfterCooldown { get; set; }

    /// <summary>
    /// Cooldown period in minutes before auto-disabled jobs are re-enabled.
    /// Only used if AutoReEnableAfterCooldown is true.
    /// Default: 30 minutes
    /// </summary>
    public int AutoReEnableCooldownMinutes { get; set; }
}