using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.EntityBases.Concrete.Auditing;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Domain.JsonModels;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;

namespace Milvasoft.Milvaion.Sdk.Domain;

/// <summary>
/// Entity representing a single execution instance of a scheduled job.
/// Tracks the lifecycle of each job trigger with correlation for observability.
/// </summary>
/// <remarks>
/// Read path indexes for this table are maintained in <c>Milvaion.Api/StaticFiles/SQL/indexes.sql</c> rather than
/// as attributes here, so that covering and partial indexes can be expressed properly.
/// </remarks>
[Table(SchedulerTableNames.JobOccurrences)]
[DontIndexCreationDate]
public class JobOccurrence : CreationAuditableEntity<Guid>
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
    /// Job version at execution time
    /// </summary>
    public int JobVersion { get; set; }

    /// <summary>
    /// Job-specific zombie timeout in minutes.
    /// If set, occurrences stuck in Queued status longer than this will be marked as Failed.
    /// If null, global ZombieDetector timeout (10 minutes) is used.
    /// Useful for long-running jobs that need higher timeout thresholds.
    /// </summary>
    public int? ZombieTimeoutMinutes { get; set; }

    /// <summary>
    /// Job-specific execution timeout in seconds (copied from ScheduledJob at dispatch time).
    /// Worker will cancel the job after this duration and mark it as TimedOut.
    /// If null, worker's JobConsumerConfig.ExecutionTimeoutSeconds is used as fallback.
    /// </summary>
    public int? ExecutionTimeoutSeconds { get; set; }

    /// <summary>
    /// Correlation ID for distributed tracing across services.
    /// Used for log aggregation and cross-system tracking.
    /// </summary>
    [Required]
    public Guid CorrelationId { get; set; }

    /// <summary>
    /// Identifier of the worker that processed this execution.
    /// (e.g., "worker-01", "container-abc123").
    /// </summary>
    [MaxLength(100)]
    public string WorkerId { get; set; }

    /// <summary>
    /// Current status of this execution.
    /// </summary>
    [Required]
    public JobOccurrenceStatus Status { get; set; } = JobOccurrenceStatus.Queued;

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
    /// Timestamp when this occurrence record was created (UTC).
    /// </summary>
    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Number of dispatch retry attempts (exponential backoff for RabbitMQ failures).
    /// </summary>
    public int DispatchRetryCount { get; set; } = 0;

    /// <summary>
    /// Next scheduled time for dispatch retry (null if no retry scheduled).
    /// </summary>
    public DateTime? NextDispatchRetryAt { get; set; }

    /// <summary>
    /// Last heartbeat timestamp from the worker (for zombie detection).
    /// </summary>
    public DateTime? LastHeartbeat { get; set; }

    /// <summary>
    /// External job occurrence identifier for mapping (e.g., "FireInstanceId").
    /// </summary>
    [MaxLength(500)]
    public string ExternalJobId { get; set; }

    /// <summary>
    /// Occurrence status change history stored as JSONB array.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public List<OccurrenceStatusChangeLog> StatusChangeLogs { get; set; } = [];

    /// <summary>
    /// Navigation property to the parent job.
    /// </summary>
    [ForeignKey(nameof(JobId))]
    public virtual ScheduledJob Job { get; set; }

    /// <summary>
    /// Log entries associated with this job occurrence.
    /// </summary>
    public virtual List<JobOccurrenceLog> Logs { get; set; }

    /// <summary>
    /// Workflow run ID if this occurrence was triggered as a workflow step. Null for standalone jobs.
    /// </summary>
    public Guid? WorkflowRunId { get; set; }

    /// <summary>
    /// Workflow step ID if this occurrence was triggered as a workflow step. Null for standalone jobs.
    /// </summary>
    public Guid? WorkflowStepId { get; set; }

    /// <summary>
    /// Workflow step orchestration status (Pending, Running, Completed, Failed, Skipped, Cancelled, Delayed).
    /// Null for standalone job occurrences.
    /// </summary>
    public WorkflowStepStatus? StepStatus { get; set; }

    /// <summary>
    /// Number of retry attempts for this workflow step within its run.
    /// </summary>
    public int StepRetryCount { get; set; } = 0;

    /// <summary>
    /// Scheduled dispatch time for delayed workflow steps.
    /// </summary>
    public DateTime? StepScheduledAt { get; set; }

    /// <summary>
    /// Navigation to the parent workflow run (only set for workflow step occurrences).
    /// </summary>
    public virtual WorkflowRun WorkflowRun { get; set; }

    public static class Projections
    {
        public static Expression<Func<JobOccurrence, JobOccurrence>> AddFailedOccurrence { get; } = s => new JobOccurrence
        {
            Id = s.Id,
            Status = s.Status,
            WorkerId = s.WorkerId,
            CorrelationId = s.CorrelationId,
            StartTime = s.StartTime,
            CreatedAt = s.CreatedAt,
        };

        public static Expression<Func<JobOccurrence, JobOccurrence>> RetryFailed { get; } = s => new JobOccurrence
        {
            Id = s.Id,
            JobId = s.JobId,
            Status = s.Status,
            Exception = s.Exception,
            NextDispatchRetryAt = s.NextDispatchRetryAt,
            CorrelationId = s.CorrelationId,
            DispatchRetryCount = s.DispatchRetryCount,
        };

        public static Expression<Func<JobOccurrence, JobOccurrence>> UpdateStatus { get; } = s => new JobOccurrence
        {
            Id = s.Id,
            Status = s.Status,
            StatusChangeLogs = s.StatusChangeLogs,
            WorkerId = s.WorkerId,
            StartTime = s.StartTime,
            EndTime = s.EndTime,
            DurationMs = s.DurationMs,
            Result = s.Result,
            Exception = s.Exception,
            LastHeartbeat = s.LastHeartbeat,
            CorrelationId = s.CorrelationId,
            JobId = s.JobId,
            JobName = s.JobName,
            WorkflowRunId = s.WorkflowRunId,
            StepStatus = s.StepStatus,
        };

        public static Expression<Func<JobOccurrence, JobOccurrence>> RecoverLostJob { get; } = s => new JobOccurrence
        {
            Id = s.Id,
            Status = s.Status,
            StatusChangeLogs = s.StatusChangeLogs,
            WorkerId = s.WorkerId,
            StartTime = s.StartTime,
            EndTime = s.EndTime,
            DurationMs = s.DurationMs,
            Result = s.Result,
            Exception = s.Exception,
            LastHeartbeat = s.LastHeartbeat,
            CorrelationId = s.CorrelationId,
            JobId = s.JobId,
            JobName = s.JobName,
        };

        public static Expression<Func<JobOccurrence, JobOccurrence>> DetectZombie { get; } = s => new JobOccurrence
        {
            Id = s.Id,
            CreatedAt = s.CreatedAt,
            Status = s.Status,
            StatusChangeLogs = s.StatusChangeLogs,
            WorkerId = s.WorkerId,
            StartTime = s.StartTime,
            EndTime = s.EndTime,
            DurationMs = s.DurationMs,
            Result = s.Result,
            Exception = s.Exception,
            LastHeartbeat = s.LastHeartbeat,
            CorrelationId = s.CorrelationId,
            JobId = s.JobId,
            JobName = s.JobName,
            ZombieTimeoutMinutes = s.ZombieTimeoutMinutes
        };
    }
}
