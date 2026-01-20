using Milvasoft.Attributes.Annotations;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.ScheduledJobDtos;

/// <summary>
/// Data transfer object for scheduledjob list.
/// </summary>
[Translate]
public class JobOccurrenceDetailDto : MilvaionBaseDto<Guid>
{
    /// <summary>
    /// Type name of job.
    /// </summary>
    public string JobName { get; set; }

    /// <summary>
    /// Reference to the parent scheduled job definition.
    /// </summary>
    public Guid JobId { get; set; }

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
    /// Full exception details if job failed (stack trace, inner exceptions).
    /// </summary>
    public string Exception { get; set; }

    /// <summary>
    /// Structured logs from job execution stored as JSONB array.
    /// Each entry contains timestamp, level, message, and optional data.
    /// </summary>
    public List<OccurrenceLog> Logs { get; set; }

    /// <summary>
    /// Occurrence status change history stored as JSONB array.
    /// </summary>
    public List<OccurrenceStatusChangeLog> StatusChangeLogs { get; set; }

    /// <summary>
    /// Timestamp when this occurrence record was created (UTC).
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Last heartbeat timestamp from the worker (for zombie detection).
    /// </summary>
    public DateTime? LastHeartbeat { get; set; }

    /// <summary>
    /// Running job version at the time of execution.
    /// </summary>
    public int JobVersion { get; set; }

    /// <summary>
    /// Projection expression for mapping ScheduledJob scheduledjob to ScheduledJobListDto.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<JobOccurrence, JobOccurrenceDetailDto>> Projection { get; } = r => new JobOccurrenceDetailDto
    {
        Id = r.Id,
        JobName = r.JobName,
        CorrelationId = r.CorrelationId,
        JobId = r.JobId,
        WorkerId = r.WorkerId,
        Status = r.Status,
        StartTime = r.StartTime,
        EndTime = r.EndTime,
        DurationMs = r.DurationMs,
        Result = r.Result,
        Exception = r.Exception,
        CreatedAt = r.CreatedAt,
        Logs = r.Logs,
        StatusChangeLogs = r.StatusChangeLogs,
        LastHeartbeat = r.LastHeartbeat,
        JobVersion = r.JobVersion
    };
}
