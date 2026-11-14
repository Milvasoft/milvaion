using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.EntityBases.Concrete.Auditing;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;

namespace Milvasoft.Milvaion.Sdk.Domain;

/// <summary>
/// Represents a single execution instance of a workflow.
/// Tracks the lifecycle of the entire workflow DAG execution.
/// </summary>
[Table(SchedulerTableNames.WorkflowRuns)]
[DontIndexCreationDate]
public class WorkflowRun : CreationAuditableEntity<Guid>, IProjectScoped
{
    /// <summary>
    /// Id of the owning project. See <see cref="IProjectScoped"/>.
    /// </summary>
    public int ProjectId { get; set; }

    /// <summary>
    /// Parent workflow definition ID.
    /// </summary>
    [Required]
    public Guid WorkflowId { get; set; }

    /// <summary>
    /// Workflow version at execution time.
    /// </summary>
    public int WorkflowVersion { get; set; }

    /// <summary>
    /// Correlation ID for distributed tracing across all steps.
    /// </summary>
    [Required]
    public Guid CorrelationId { get; set; }

    /// <summary>
    /// Current status of this workflow run.
    /// </summary>
    [Required]
    public WorkflowStatus Status { get; set; } = WorkflowStatus.Pending;

    /// <summary>
    /// Timestamp when the workflow run started.
    /// </summary>
    public DateTime? StartTime { get; set; }

    /// <summary>
    /// Time a run triggered for later starts at (UTC). Null means the run starts as soon as the engine picks it up.
    /// Until then the run stays <see cref="WorkflowStatus.Pending"/>; it can be rescheduled or cancelled.
    /// </summary>
    public DateTime? ScheduledAt { get; set; }

    /// <summary>
    /// Timestamp when the workflow run ended.
    /// </summary>
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// Total duration in milliseconds.
    /// </summary>
    public long? DurationMs { get; set; }

    /// <summary>
    /// Trigger reason / description.
    /// </summary>
    [MaxLength(500)]
    public string TriggerReason { get; set; }

    /// <summary>
    /// Error message if the workflow failed.
    /// </summary>
    public string Error { get; set; }

    /// <summary>
    /// Per-step job data overrides provided at trigger time.
    /// Key is the <see cref="WorkflowStepDefinition.Id"/> (WorkflowStepId), value is JSON job data.
    /// Merged on top of base job data, design-time <see cref="WorkflowStepDefinition.JobDataOverride"/>,
    /// and <see cref="GlobalJobData"/> — highest priority at dispatch time.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public Dictionary<Guid, string> StepJobData { get; set; }

    /// <summary>
    /// Timestamp when this record was created.
    /// </summary>
    [Required]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Navigation to the parent workflow.
    /// </summary>
    [ForeignKey(nameof(WorkflowId))]
    public virtual Workflow Workflow { get; set; }

    /// <summary>
    /// Step occurrences belonging to this workflow run.
    /// </summary>
    [CascadeOnDelete]
    public virtual List<JobOccurrence> StepOccurrences { get; set; } = [];

    public static class Projections
    {
        public static Expression<Func<WorkflowRun, WorkflowRun>> List { get; } = r => new WorkflowRun
        {
            Id = r.Id,
            ProjectId = r.ProjectId,
            WorkflowId = r.WorkflowId,
            WorkflowVersion = r.WorkflowVersion,
            CorrelationId = r.CorrelationId,
            Status = r.Status,
            StartTime = r.StartTime,
            ScheduledAt = r.ScheduledAt,
            EndTime = r.EndTime,
            DurationMs = r.DurationMs,
            TriggerReason = r.TriggerReason,
            CreatedAt = r.CreatedAt,
        };
    }
}
