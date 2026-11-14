using Milvasoft.Milvaion.Sdk.Domain.Enums;

namespace Milvasoft.Milvaion.Sdk.Domain.JsonModels;

/// <summary>
/// Represents a snapshot of a workflow definition, including its properties and steps, used for serialization and transfer.
/// </summary>
public class WorkflowSnapshot
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public string Tags { get; set; }
    public bool IsActive { get; set; }
    public WorkflowFailureStrategy FailureStrategy { get; set; }
    public int MaxStepRetries { get; set; }
    public int MaxRetryAfter { get; set; }
    public int? TimeoutSeconds { get; set; }
    public int Version { get; set; }
    public string CronExpression { get; set; }
    public DateTime? LastScheduledRunAt { get; set; }
    public DateTime? CreationDate { get; set; }
    public string CreatorUserName { get; set; }
    public DateTime? LastModificationDate { get; set; }
    public string LastModifierUserName { get; set; }
    public List<WorkflowStepSnapshot> Steps { get; set; }
    public List<WorkflowEdgeSnapshot> Edges { get; set; }
}

/// <summary>
/// Represents a snapshot of a workflow definition, including its properties and steps, used for serialization and transfer.
/// </summary>
public class WorkflowStepSnapshot
{
    /// <summary>
    /// Id of step.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Parent workflow ID.
    /// </summary>
    public Guid WorkflowId { get; set; }

    /// <summary>
    /// The scheduled job this step executes.
    /// </summary>
    public WorkflowNodeType NodeType { get; set; }

    /// <summary>
    /// The scheduled job this step executes.
    /// </summary>
    public Guid? JobId { get; set; }

    /// <summary>
    /// Job version for this step (for reference, not necessarily unique). Used for visualization and debugging.
    /// </summary>
    public int JobVersion { get; set; }

    /// <summary>
    /// Job name for this step (for reference, not necessarily unique). Used for visualization and debugging.
    /// </summary>
    public string JobName { get; set; }

    /// <summary>
    /// User-friendly label for this step (e.g., "Extract Data", "Send Notification").
    /// </summary>
    public string StepName { get; set; }

    /// <summary>
    /// Sort order / visual ordering hint for the step.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// Node specific configuration stored as JSON.
    /// </summary>
    public string NodeConfigJson { get; set; }

    /// <summary>
    /// JSON mapping definition for passing data from parent steps to this step's job data.
    /// Format: { "sourceStepId:jsonPath": "targetJsonPath", ... }
    /// Null means use the job's default data.
    /// </summary>
    public string DataMappings { get; set; }

    /// <summary>
    /// Delay in seconds before executing this step after dependencies complete.
    /// 0 means execute immediately.
    /// </summary>
    public int DelaySeconds { get; set; } = 0;

    /// <summary>
    /// Override job data for this step (JSON). If null, uses the job's default data.
    /// </summary>
    public string JobDataOverride { get; set; }

    /// <summary>
    /// X coordinate for DAG visualization.
    /// </summary>
    public double? PositionX { get; set; }

    /// <summary>
    /// Y coordinate for DAG visualization.
    /// </summary>
    public double? PositionY { get; set; }
}

/// <summary>
/// Represents a snapshot of a workflow edge.
/// </summary>
public class WorkflowEdgeSnapshot
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    public Guid SourceStepId { get; set; }
    public Guid TargetStepId { get; set; }
    public string SourcePort { get; set; }
    public string TargetPort { get; set; }
    public string Label { get; set; }
    public int Order { get; set; }
    public string EdgeConfigJson { get; set; }
}