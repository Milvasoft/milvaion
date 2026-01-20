namespace Milvaion.Application.Dtos.ScheduledJobDtos;

/// <summary>
/// Job auto-disable settings stored as JSON in ScheduledJob entity.
/// </summary>
public class UpsertJobAutoDisableSettings
{
    /// <summary>
    /// Whether auto-disable feature is enabled for this specific job.
    /// If null, uses global setting from configuration.
    /// Set to false to never auto-disable this job regardless of failures.
    /// </summary>
    public bool? Enabled { get; set; } = true;

    /// <summary>
    /// Job-specific threshold for consecutive failures before auto-disable.
    /// If null, uses global setting from configuration.
    /// </summary>
    public int? Threshold { get; set; }
}