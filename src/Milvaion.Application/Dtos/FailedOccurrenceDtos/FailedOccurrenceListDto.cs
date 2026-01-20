using Milvasoft.Attributes.Annotations;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.FailedOccurrenceDtos;

/// <summary>
/// Data transfer object for failedjob list.
/// </summary>
[Translate]
public class FailedOccurrenceListDto : MilvaionBaseDto<Guid>
{
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
    /// Timestamp when job was moved to DLQ.
    /// </summary>
    public DateTime FailedAt { get; set; }

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
    /// Number of retry attempts before failure.
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Projection expression for mapping FailedOccurrence failedjob to FailedOccurrenceListDto.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<FailedOccurrence, FailedOccurrenceListDto>> Projection { get; } = r => new FailedOccurrenceListDto
    {
        Id = r.Id,
        JobDisplayName = r.JobDisplayName,
        JobNameInWorker = r.JobNameInWorker,
        WorkerId = r.WorkerId,
        FailedAt = r.FailedAt,
        FailureType = r.FailureType,
        Resolved = r.Resolved,
        RetryCount = r.RetryCount,
        ResolvedAt = r.ResolvedAt
    };
}
