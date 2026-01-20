using Milvasoft.Attributes.Annotations;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.FailedOccurrenceDtos;

/// <summary>
/// Data transfer object for failedjob details.
/// </summary>
[Translate]
[ExcludeFromMetadata]
public class FailedOccurrenceDetailDto : MilvaionBaseDto<Guid>
{
    /// <summary>
    /// Reference to the original scheduled job.
    /// </summary>
    public Guid JobId { get; set; }

    /// <summary>
    /// Reference to the failed job occurrence.
    /// </summary>
    public Guid OccurrenceId { get; set; }

    /// <summary>
    /// Correlation ID for distributed tracing.
    /// </summary>
    public Guid CorrelationId { get; set; }

    /// <summary>
    /// Display name of the job (copied from ScheduledJob for quick reference).
    /// </summary>
    public string JobDisplayName { get; set; }

    /// <summary>
    /// Job type/name in worker (e.g., "SendEmailJob").
    /// </summary>
    public string JobNameInWorker { get; set; }

    /// <summary>
    /// Worker ID that last attempted to process this job.
    /// </summary>
    public string WorkerId { get; set; }

    /// <summary>
    /// JSON serialized job data (payload).
    /// </summary>
    public string JobData { get; set; }

    /// <summary>
    /// Final exception/error message before moving to DLQ.
    /// </summary>
    public string Exception { get; set; }

    /// <summary>
    /// Timestamp when job was moved to DLQ.
    /// </summary>
    public DateTime FailedAt { get; set; }

    /// <summary>
    /// Number of retry attempts before failure.
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Type of failure (for categorization and analysis).
    /// </summary>
    public FailureType FailureType { get; set; }

    /// <summary>
    /// Indicates whether this failed job has been reviewed and resolved.
    /// </summary>
    public bool Resolved { get; set; } = false;

    /// <summary>
    /// Timestamp when job was marked as resolved.
    /// </summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>
    /// Username who resolved this failed job.
    /// </summary>
    public string ResolvedBy { get; set; }

    /// <summary>
    /// Resolution notes/comments.
    /// </summary>
    public string ResolutionNote { get; set; }

    /// <summary>
    /// Action taken to resolve (e.g., "Retried manually", "Fixed data and re-queued", "Ignored - invalid data").
    /// </summary>
    public string ResolutionAction { get; set; }

    /// <summary>
    /// Scheduled execution time of original job.
    /// </summary>
    public DateTime? OriginalExecuteAt { get; set; }

    /// <summary>
    /// Information about record audit.
    /// </summary>
    public AuditDto<Guid> AuditInfo { get; set; }

    /// <summary>
    /// Projection expression for mapping FailedOccurrence failedjob to FailedOccurrenceDetailDto.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<FailedOccurrence, FailedOccurrenceDetailDto>> Projection { get; } = r => new FailedOccurrenceDetailDto
    {
        Id = r.Id,
        JobId = r.JobId,
        OccurrenceId = r.OccurrenceId,
        CorrelationId = r.CorrelationId,
        JobDisplayName = r.JobDisplayName,
        JobNameInWorker = r.JobNameInWorker,
        WorkerId = r.WorkerId,
        JobData = r.JobData,
        Exception = r.Exception,
        FailedAt = r.FailedAt,
        RetryCount = r.RetryCount,
        FailureType = r.FailureType,
        Resolved = r.Resolved,
        ResolvedAt = r.ResolvedAt,
        ResolvedBy = r.ResolvedBy,
        ResolutionNote = r.ResolutionNote,
        ResolutionAction = r.ResolutionAction,
        OriginalExecuteAt = r.OriginalExecuteAt,
        AuditInfo = new AuditDto<Guid>(r)
    };
}
