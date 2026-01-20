using Microsoft.EntityFrameworkCore;
using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.EntityBases.Concrete.Auditing;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;

namespace Milvasoft.Milvaion.Sdk.Domain;

/// <summary>
/// Entity for tracking failed jobs that were moved to Dead Letter Queue.
/// Used for manual review and recovery after max retry attempts exceeded.
/// </summary>
[Table(SchedulerTableNames.FailedOccurrences)]
[Index(nameof(Resolved), nameof(FailedAt), IsDescending = [false, true])] // For filtering resolved/unresolved jobs and ordering by FailedAt
[Index(nameof(FailureType), nameof(Resolved))] // For filtering by failure type and resolution status
[DontIndexCreationDate]
public class FailedOccurrence : CreationAuditableEntity<Guid>
{
    /// <summary>
    /// Reference to the original scheduled job.
    /// </summary>
    [Required]
    public Guid JobId { get; set; }

    /// <summary>
    /// Reference to the failed job occurrence.
    /// </summary>
    [Required]
    public Guid OccurrenceId { get; set; }

    /// <summary>
    /// Correlation ID for distributed tracing.
    /// </summary>
    [Required]
    public Guid CorrelationId { get; set; }

    /// <summary>
    /// Display name of the job (copied from ScheduledJob for quick reference).
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string JobDisplayName { get; set; }

    /// <summary>
    /// Job type/name in worker (e.g., "SendEmailJob").
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string JobNameInWorker { get; set; }

    /// <summary>
    /// Worker ID that last attempted to process this job.
    /// </summary>
    [MaxLength(100)]
    public string WorkerId { get; set; }

    /// <summary>
    /// JSON serialized job data (payload).
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string JobData { get; set; }

    /// <summary>
    /// Final exception/error message before moving to DLQ.
    /// </summary>
    [Column(TypeName = "text")]
    public string Exception { get; set; }

    /// <summary>
    /// Timestamp when job was moved to DLQ.
    /// </summary>
    [Required]
    public DateTime FailedAt { get; set; }

    /// <summary>
    /// Number of retry attempts before failure.
    /// </summary>
    [Required]
    public int RetryCount { get; set; }

    /// <summary>
    /// Type of failure (for categorization and analysis).
    /// </summary>
    [Required]
    public FailureType FailureType { get; set; } = FailureType.Unknown;

    /// <summary>
    /// Indicates whether this failed job has been reviewed and resolved.
    /// </summary>
    [Required]
    public bool Resolved { get; set; } = false;

    /// <summary>
    /// Timestamp when job was marked as resolved.
    /// </summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>
    /// Username who resolved this failed job.
    /// </summary>
    [MaxLength(100)]
    public string ResolvedBy { get; set; }

    /// <summary>
    /// Resolution notes/comments.
    /// </summary>
    public string ResolutionNote { get; set; }

    /// <summary>
    /// Action taken to resolve (e.g., "Retried manually", "Fixed data and re-queued", "Ignored - invalid data").
    /// </summary>
    [MaxLength(200)]
    public string ResolutionAction { get; set; }

    /// <summary>
    /// Scheduled execution time of original job.
    /// </summary>
    public DateTime? OriginalExecuteAt { get; set; }

    /// <summary>
    /// Navigation property to the scheduled job.
    /// </summary>
    public virtual ScheduledJob Job { get; set; }

    /// <summary>
    /// Navigation property to the failed occurrence.
    /// </summary>
    public virtual JobOccurrence Occurrence { get; set; }

    public static class Projections
    {
        public static Expression<Func<ScheduledJob, ScheduledJob>> TagList { get; } = s => new ScheduledJob
        {
            Id = s.Id,
            Tags = s.Tags
        };
    }
}
