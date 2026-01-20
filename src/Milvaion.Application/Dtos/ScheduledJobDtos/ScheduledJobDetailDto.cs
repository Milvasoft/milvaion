using Milvasoft.Attributes.Annotations;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.ScheduledJobDtos;

/// <summary>
/// Data transfer object for scheduledjob details.
/// </summary>
[Translate]
[ExcludeFromMetadata]
public class ScheduledJobDetailDto : MilvaionBaseDto<Guid>
{
    /// <summary>
    /// Display name of the scheduled job.
    /// </summary>
    public string DisplayName { get; set; }

    /// <summary>
    /// Description of the scheduled job.
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// Comma separated Tags of the scheduled job.
    /// </summary>
    public string Tags { get; set; }

    /// <summary>
    /// Target worker ID that should execute this job.
    /// </summary>
    public string WorkerId { get; set; }

    /// <summary>
    /// Type identifier for the job handler (e.g., "SendEmailJob", "ProcessOrderJob").
    /// </summary>
    public string JobType { get; set; }

    /// <summary>
    /// JSON serialized payload data required for job execution.
    /// </summary>
    public string JobData { get; set; }

    /// <summary>
    /// Scheduled execution time (UTC). Dispatcher will trigger the job at or after this time.
    /// For recurring jobs, this is automatically updated to the next execution time based on CronExpression.
    /// </summary>
    public DateTime? ExecuteAt { get; set; }

    /// <summary>
    /// Cron expression for recurring job scheduling (e.g., "0 9 * * MON" for every Monday at 9 AM).
    /// Supports standard cron format (minute, hour, day of month, month, day of week).
    /// If null, the job is a one-time job that executes at ExecuteAt and is not rescheduled.
    /// </summary>
    public string CronExpression { get; set; }

    /// <summary>
    /// Indicates whether the job is active and should be processed by the dispatcher.
    /// Inactive jobs are skipped during scheduling. Users can toggle this via dashboard.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Defines behavior when a job is triggered while a previous occurrence is still running.
    /// </summary>
    public ConcurrentExecutionPolicy ConcurrentExecutionPolicy { get; set; }

    /// <summary>
    /// Information about record audit.
    /// </summary>
    public AuditDto<Guid> AuditInfo { get; set; }

    /// <summary>
    /// Total execution duration in milliseconds.
    /// Calculated as (EndTime - StartTime).
    /// </summary>
    public double? AvarageDuration { get; set; }

    /// <summary>
    /// Success rate percentage.
    /// </summary>
    public long? SuccessRate { get; set; }

    /// <summary>
    /// Total execution count.
    /// </summary>
    public long? TotalExecutions { get; set; }

    /// <summary>
    /// Job-specific zombie timeout in minutes.
    /// If set, occurrences stuck in Queued status longer than this will be marked as Failed.
    /// If null, global ZombieDetector timeout (10 minutes) is used.
    /// Useful for long-running jobs that need higher timeout thresholds.
    /// </summary>
    public int? ZombieTimeoutMinutes { get; set; }

    /// <summary>
    /// Job-specific execution timeout in seconds.
    /// If set, worker will cancel the job after this duration and mark it as TimedOut.
    /// If null, worker's JobConsumerConfig.ExecutionTimeoutSeconds is used as fallback.
    /// Default: null (use worker config, typically 3600 seconds = 1 hour).
    /// </summary>
    public int? ExecutionTimeoutSeconds { get; set; }

    /// <summary>
    /// Version of the job associated with this scheduled job.
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// Versions of the job associated with this scheduled job.
    /// </summary>
    public List<string> JobVersions { get; set; }

    /// <summary>
    /// Auto disable settings for the scheduled job.
    /// </summary>
    public JobAutoDisableSettings AutoDisableSettings { get; set; }

    /// <summary>
    /// Projection expression for mapping ScheduledJob scheduledjob to ScheduledJobDetailDto.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<ScheduledJob, ScheduledJobDetailDto>> Projection { get; } = r => new ScheduledJobDetailDto
    {
        Id = r.Id,
        DisplayName = r.DisplayName,
        Description = r.Description,
        Tags = r.Tags,
        WorkerId = r.WorkerId,
        JobType = r.JobNameInWorker,
        JobData = r.JobData,
        ExecuteAt = r.ExecuteAt,
        CronExpression = r.CronExpression,
        IsActive = r.IsActive,
        ConcurrentExecutionPolicy = r.ConcurrentExecutionPolicy,
        AvarageDuration = r.Occurrences.Average(o => o.DurationMs),
        SuccessRate = r.Occurrences.Count == 0 ? 0 : r.Occurrences.Count(o => o.Status == JobOccurrenceStatus.Completed) * 100 / r.Occurrences.Count,
        TotalExecutions = r.Occurrences.Count,
        ZombieTimeoutMinutes = r.ZombieTimeoutMinutes,
        ExecutionTimeoutSeconds = r.ExecutionTimeoutSeconds,
        Version = r.Version,
        JobVersions = r.JobVersions,
        AutoDisableSettings = r.AutoDisableSettings,
        AuditInfo = new AuditDto<Guid>(r)
    };
}
