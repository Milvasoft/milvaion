using Milvasoft.Attributes.Annotations;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.ScheduledJobDtos;

/// <summary>
/// Data transfer object for scheduledjob list.
/// </summary>
[Translate]
public class JobOccurrenceListDto : MilvaionBaseDto<Guid>
{
    /// <summary>
    /// Parent job identifier.
    /// </summary>
    public Guid JobId { get; set; }

    /// <summary>
    /// Job display name of job.
    /// </summary>
    public string JobDisplayName { get; set; }

    /// <summary>
    /// Tags of job.
    /// </summary>
    public string JobTags { get; set; }

    /// <summary>
    /// Type name of job.
    /// </summary>
    public string JobName { get; set; }

    /// <summary>
    /// Correlation ID for distributed tracing across services.
    /// Used for log aggregation and cross-system tracking.
    /// </summary>
    public Guid CorrelationId { get; set; }

    /// <summary>
    /// Identifier of the worker that processed this execution.
    /// (e.g., "worker-01", "container-abc123").
    /// </summary>
    public string WorkerId { get; set; }

    /// <summary>
    /// Current status of this execution.
    /// </summary>
    public JobOccurrenceStatus Status { get; set; }

    /// <summary>
    /// Timestamp when job execution started (UTC).
    /// </summary>
    public DateTime? StartTime { get; set; }

    /// <summary>
    /// Timestamp when job execution finished (UTC).
    /// </summary>
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// Total execution duration in milliseconds.
    /// Calculated as (EndTime - StartTime).
    /// </summary>
    public long? DurationMs { get; set; }

    /// <summary>
    /// Result message from job execution (success message or summary).
    /// </summary>
    public string Result { get; set; }

    /// <summary>
    /// Timestamp when this occurrence record was created (UTC).
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Projection expression for mapping ScheduledJob scheduledjob to ScheduledJobListDto.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<JobOccurrence, JobOccurrenceListDto>> Projection { get; } = r => new JobOccurrenceListDto
    {
        Id = r.Id,
        JobId = r.JobId,
        JobName = r.JobName,
        CorrelationId = r.CorrelationId,
        WorkerId = r.WorkerId,
        Status = r.Status,
        StartTime = r.StartTime,
        EndTime = r.EndTime,
        DurationMs = r.DurationMs,
        Result = r.Result,
        CreatedAt = r.CreatedAt,
        JobDisplayName = r.Job.DisplayName,
        JobTags = r.Job.Tags
    };
}
