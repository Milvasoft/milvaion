using Milvasoft.Attributes.Annotations;

namespace Milvaion.Application.Dtos.WorkflowDtos;

/// <summary>
/// Data transfer object for workflow detail.
/// </summary>
[Translate]
[ExcludeFromMetadata]
public class WorkflowDetailDto : MilvaionBaseDto<Guid>
{
    /// <summary>
    /// Display name of the workflow.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Description of the workflow.
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// Tags for categorization.
    /// </summary>
    public string Tags { get; set; }

    /// <summary>
    /// Whether this workflow is active.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Failure handling strategy.
    /// </summary>
    public WorkflowFailureStrategy FailureStrategy { get; set; }

    /// <summary>
    /// Maximum step retries.
    /// </summary>
    public int MaxStepRetries { get; set; }

    /// <summary>
    /// Maximum reruns of a step at the time its job asked for (retry-after), per step and run.
    /// </summary>
    public int MaxRetryAfter { get; set; }

    /// <summary>
    /// Timeout in seconds for entire workflow.
    /// </summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>
    /// Version of the workflow.
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// Cron expression for automatic recurring execution.
    /// </summary>
    public string CronExpression { get; set; }

    /// <summary>
    /// Last time the workflow was triggered by cron scheduler.
    /// </summary>
    public DateTime? LastScheduledRunAt { get; set; }

    /// <summary>
    /// Information about record audit.
    /// </summary>
    public AuditDto<Guid> AuditInfo { get; set; }

    /// <summary>
    /// Steps in this workflow.
    /// </summary>
    public List<WorkflowStepDto> Steps { get; set; } = [];

    /// <summary>
    /// Edges in this workflow.
    /// </summary>
    public List<WorkflowEdgeDto> Edges { get; set; } = [];

    /// <summary>
    /// Workflow versions history (serialized workflow snapshots with steps).
    /// Each entry is a JSON snapshot of the workflow definition before it was updated.
    /// </summary>
    public List<WorkflowSnapshot> WorkflowVersions { get; set; } = [];
}

/// <summary>
/// DTO for a workflow step.
/// </summary>
public class WorkflowStepDto
{
    /// <summary>
    /// Step ID.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Node type.
    /// </summary>
    public WorkflowNodeType NodeType { get; set; }

    /// <summary>
    /// The scheduled job this step executes.
    /// </summary>
    public Guid? JobId { get; set; }

    /// <summary>
    /// Job display name.
    /// </summary>
    public string JobDisplayName { get; set; }

    /// <summary>
    /// Worker ID that owns the job (used for schema lookup in UI).
    /// </summary>
    public string WorkerId { get; set; }

    /// <summary>
    /// Job name in worker (used for schema lookup in UI).
    /// </summary>
    public string JobNameInWorker { get; set; }

    /// <summary>
    /// User-friendly label for this step.
    /// </summary>
    public string StepName { get; set; }

    /// <summary>
    /// Sort order.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// Node specific configuration stored as JSON.
    /// </summary>
    public string NodeConfigJson { get; set; }

    /// <summary>
    /// Data mapping definitions.
    /// </summary>
    public string DataMappings { get; set; }

    /// <summary>
    /// Delay in seconds before executing.
    /// </summary>
    public int DelaySeconds { get; set; }

    /// <summary>
    /// Override job data.
    /// </summary>
    public string JobDataOverride { get; set; }

    /// <summary>
    /// X position for DAG visualization.
    /// </summary>
    public double? PositionX { get; set; }

    /// <summary>
    /// Y position for DAG visualization.
    /// </summary>
    public double? PositionY { get; set; }
}

/// <summary>
/// DTO for a workflow edge.
/// </summary>
public class WorkflowEdgeDto
{
    /// <summary>
    /// ID of the source step.
    /// </summary>
    public Guid SourceStepId { get; set; }

    /// <summary>
    /// ID of the target step.
    /// </summary>
    public Guid TargetStepId { get; set; }

    /// <summary>
    /// Source port identifier for the connection.
    /// </summary>
    public string SourcePort { get; set; }

    /// <summary>
    /// Target port identifier for the connection.
    /// </summary>
    public string TargetPort { get; set; }

    /// <summary>
    /// Display label for the edge.
    /// </summary>
    public string Label { get; set; }

    /// <summary>
    /// Sort order for edge evaluation.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// Edge specific configuration stored as JSON.
    /// </summary>
    public string EdgeConfigJson { get; set; }
}
