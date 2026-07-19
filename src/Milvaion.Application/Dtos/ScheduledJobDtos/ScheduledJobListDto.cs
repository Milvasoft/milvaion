using Milvasoft.Attributes.Annotations;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.ScheduledJobDtos;

/// <summary>
/// Data transfer object for scheduledjob list.
/// </summary>
[Translate]
public class ScheduledJobListDto : MilvaionBaseDto<Guid>
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
    /// Type identifier for the job handler (e.g., "SendEmailJob", "ProcessOrderJob").
    /// </summary>
    public string JobType { get; set; }

    /// <summary>
    /// JSON serialized payload data required for job execution.
    /// </summary>
    public string JobData { get; set; }

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
    /// When a one-time job was dispatched (UTC). Null for recurring jobs and for one-time
    /// jobs that have not run yet.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="IsActive"/>: a finished one-time job is still active, it
    /// simply has nothing left to run. Without this the list would show it as ready to go
    /// forever.
    /// </remarks>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Defines behavior when a job is triggered while a previous occurrence is still running.
    /// </summary>
    public ConcurrentExecutionPolicy ConcurrentExecutionPolicy { get; set; }

    /// <summary>
    /// Latest run time of the job.
    /// </summary>
    public string Tags { get; set; }

    /// <summary>
    /// Indicates whether this job is from an external scheduler (Quartz, Hangfire, etc.).
    /// External jobs are not dispatched by Milvaion - they only report their occurrences for monitoring.
    /// </summary>
    public bool IsExternal { get; set; }

    /// <summary>
    /// Projection expression for mapping ScheduledJob to ScheduledJobListDto.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<ScheduledJob, ScheduledJobListDto>> Projection { get; } = r => new ScheduledJobListDto
    {
        Id = r.Id,
        DisplayName = r.DisplayName,
        Description = r.Description,
        CronExpression = r.CronExpression,
        JobData = r.JobData,
        JobType = r.JobNameInWorker,
        IsActive = r.IsActive,
        CompletedAt = r.CompletedAt,
        ConcurrentExecutionPolicy = r.ConcurrentExecutionPolicy,
        Tags = r.Tags,
        IsExternal = r.IsExternal
    };
}