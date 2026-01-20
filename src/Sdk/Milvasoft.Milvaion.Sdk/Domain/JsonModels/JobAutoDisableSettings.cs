using System.ComponentModel.DataAnnotations;

namespace Milvasoft.Milvaion.Sdk.Domain.JsonModels;

/// <summary>
/// Job auto-disable settings stored as JSON in ScheduledJob entity.
/// </summary>
public class JobAutoDisableSettings
{
    /// <summary>
    /// Number of consecutive failures for this job.
    /// Reset to 0 when job completes successfully.
    /// Used by auto-disable feature to deactivate failing jobs.
    /// </summary>
    public int ConsecutiveFailureCount { get; set; } = 0;

    /// <summary>
    /// Timestamp of the last failure. Used to track failure patterns.
    /// </summary>
    public DateTime? LastFailureTime { get; set; }

    /// <summary>
    /// Timestamp when the job was auto-disabled due to consecutive failures.
    /// Null if job was never auto-disabled or was manually re-enabled.
    /// </summary>
    public DateTime? DisabledAt { get; set; }

    /// <summary>
    /// Reason for auto-disable. Contains failure count and last error info.
    /// </summary>
    [MaxLength(500)]
    public string DisableReason { get; set; }

    /// <summary>
    /// Whether auto-disable feature is enabled for this specific job.
    /// If null, uses global setting from configuration.
    /// Set to false to never auto-disable this job regardless of failures.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// Job-specific threshold for consecutive failures before auto-disable.
    /// If null, uses global setting from configuration.
    /// </summary>
    public int? Threshold { get; set; }
}
