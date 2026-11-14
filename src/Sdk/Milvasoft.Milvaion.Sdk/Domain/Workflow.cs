using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.EntityBases.Concrete.Auditing;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Domain.JsonModels;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;

namespace Milvasoft.Milvaion.Sdk.Domain;

/// <summary>
/// Represents a workflow definition (DAG). A workflow chains multiple jobs together
/// with dependency edges, conditions, and data-passing rules.
/// </summary>
[Table(SchedulerTableNames.Workflows)]
[DontIndexCreationDate]
public class Workflow : AuditableEntity<Guid>, IProjectScoped
{
    /// <summary>
    /// Id of the owning project. See <see cref="IProjectScoped"/>.
    /// </summary>
    public int ProjectId { get; set; }

    /// <summary>
    /// Display name of the workflow.
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string Name { get; set; }

    /// <summary>
    /// Description of the workflow.
    /// </summary>
    [MaxLength(2000)]
    public string Description { get; set; }

    /// <summary>
    /// Comma separated tags for categorization.
    /// </summary>
    [MaxLength(500)]
    public string Tags { get; set; }

    /// <summary>
    /// Whether this workflow is active and can be triggered.
    /// </summary>
    [Required]
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Failure handling strategy for this workflow.
    /// </summary>
    [Required]
    public WorkflowFailureStrategy FailureStrategy { get; set; } = WorkflowFailureStrategy.StopOnFirstFailure;

    /// <summary>
    /// Maximum number of retries for failed steps (applies when FailureStrategy includes retry).
    /// </summary>
    public int MaxStepRetries { get; set; } = 0;

    /// <summary>
    /// Maximum number of times a step may be rerun at the time its job asked for (a retry-after error), per step and run.
    /// These reruns do not count against <see cref="MaxStepRetries"/>. Once used up, the step's failure is handled normally.
    /// </summary>
    public int MaxRetryAfter { get; set; } = 10;

    /// <summary>
    /// Maximum allowed execution duration for the entire workflow in seconds.
    /// Null means no timeout.
    /// </summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>
    /// Version of the workflow definition. Incremented on each update.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Cron expression for automatic recurring execution (e.g. "0 0 9 * * *" for every day at 9 AM).
    /// Uses 6-part cron format: second minute hour day month dayOfWeek.
    /// Null means the workflow is only triggered manually.
    /// </summary>
    [MaxLength(100)]
    public string CronExpression { get; set; }

    /// <summary>
    /// Timestamp of the last time the workflow was triggered by the cron scheduler.
    /// Used to calculate the next scheduled run.
    /// </summary>
    public DateTime? LastScheduledRunAt { get; set; }

    /// <summary>
    /// Workflow definition containing steps and edges as JSONB.
    /// Stored as a single JSON object for atomic updates and efficient loading.
    /// </summary>
    [Required]
    [Column(TypeName = "jsonb")]
    public WorkflowDefinition Definition { get; set; } = new();

    /// <summary>
    /// Workflow versions history (serialized workflow snapshots with steps).
    /// Each entry is a JSON snapshot of the workflow definition before it was updated.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public List<WorkflowSnapshot> Versions { get; set; } = [];

    /// <summary>
    /// Runs (execution instances) of this workflow.
    /// </summary>
    [CascadeOnDelete]
    public virtual List<WorkflowRun> Runs { get; set; }

    public static class Projections
    {
        public static Expression<Func<Workflow, Workflow>> List { get; } = w => new Workflow
        {
            Id = w.Id,
            ProjectId = w.ProjectId,
            Name = w.Name,
            Description = w.Description,
            Tags = w.Tags,
            IsActive = w.IsActive,
            FailureStrategy = w.FailureStrategy,
            Version = w.Version,
            CronExpression = w.CronExpression,
            CreationDate = w.CreationDate,
            CreatorUserName = w.CreatorUserName,
        };

        public static Expression<Func<Workflow, Workflow>> Detail { get; } = w => new Workflow
        {
            Id = w.Id,
            ProjectId = w.ProjectId,
            Name = w.Name,
            Description = w.Description,
            Tags = w.Tags,
            IsActive = w.IsActive,
            FailureStrategy = w.FailureStrategy,
            MaxStepRetries = w.MaxStepRetries,
            MaxRetryAfter = w.MaxRetryAfter,
            TimeoutSeconds = w.TimeoutSeconds,
            Version = w.Version,
            CronExpression = w.CronExpression,
            LastScheduledRunAt = w.LastScheduledRunAt,
            CreationDate = w.CreationDate,
            CreatorUserName = w.CreatorUserName,
            Definition = w.Definition,
            Versions = w.Versions,
        };

        public static Expression<Func<Workflow, Workflow>> Trigger { get; } = w => new Workflow
        {
            Id = w.Id,
            ProjectId = w.ProjectId,
            Name = w.Name,
            Description = w.Description,
            Tags = w.Tags,
            IsActive = w.IsActive,
            FailureStrategy = w.FailureStrategy,
            MaxStepRetries = w.MaxStepRetries,
            MaxRetryAfter = w.MaxRetryAfter,
            TimeoutSeconds = w.TimeoutSeconds,
            Version = w.Version,
            CronExpression = w.CronExpression,
            LastScheduledRunAt = w.LastScheduledRunAt,
            CreationDate = w.CreationDate,
            CreatorUserName = w.CreatorUserName,
            Definition = w.Definition,
        };

        public static Expression<Func<Workflow, Workflow>> CheckCron { get; } = w => new Workflow
        {
            Id = w.Id,
            ProjectId = w.ProjectId,
            CronExpression = w.CronExpression,
            LastScheduledRunAt = w.LastScheduledRunAt,
        };
    }
}
