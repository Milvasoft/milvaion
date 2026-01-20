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
    /// Defines behavior when a job is triggered while a previous occurrence is still running.
    /// </summary>
    public ConcurrentExecutionPolicy ConcurrentExecutionPolicy { get; set; }

    /// <summary>
    /// Latest occurrence status (computed from most recent JobOccurrence).
    /// Note: This is NOT persisted in ScheduledJob - fetched from occurrences.
    /// </summary>
    public JobOccurrenceStatus? LatestStatus { get; set; }

    /// <summary>
    /// Latest run time of the job.
    /// </summary>
    public DateTime? LatestRun { get; set; }

    /// <summary>
    /// Latest run time of the job.
    /// </summary>
    public string Tags { get; set; }

    /// <summary>
    /// Projection expression for mapping ScheduledJob to ScheduledJobListDto.
    /// Note: LatestStatus must be populated separately via join or post-query.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<ScheduledJob, ScheduledJobListDto>> Projection { get; } = r => new ScheduledJobListDto
    {
        Id = r.Id,
        DisplayName = r.DisplayName,
        CronExpression = r.CronExpression,
        JobData = r.JobData,
        JobType = r.JobNameInWorker,
        IsActive = r.IsActive,
        ConcurrentExecutionPolicy = r.ConcurrentExecutionPolicy,
        Tags = r.Tags
    };
}
