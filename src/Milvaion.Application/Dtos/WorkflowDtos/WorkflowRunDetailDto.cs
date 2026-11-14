using Milvasoft.Attributes.Annotations;

namespace Milvaion.Application.Dtos.WorkflowDtos;

/// <summary>
/// Data transfer object for workflow run detail including step run states.
/// </summary>
[Translate]
[ExcludeFromMetadata]
public class WorkflowRunDetailDto : MilvaionBaseDto<Guid>
{
    /// <summary>
    /// Parent workflow ID.
    /// </summary>
    public Guid WorkflowId { get; set; }

    /// <summary>
    /// Workflow name.
    /// </summary>
    public string WorkflowName { get; set; }

    /// <summary>
    /// Workflow version at execution time.
    /// </summary>
    public int WorkflowVersion { get; set; }

    /// <summary>
    /// Correlation ID.
    /// </summary>
    public Guid CorrelationId { get; set; }

    /// <summary>
    /// Status of the workflow run.
    /// </summary>
    public WorkflowStatus Status { get; set; }

    /// <summary>
    /// Start time.
    /// </summary>
    public DateTime? StartTime { get; set; }

    /// <summary>
    /// End time.
    /// </summary>
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// Duration in milliseconds.
    /// </summary>
    public long? DurationMs { get; set; }

    /// <summary>
    /// Trigger reason.
    /// </summary>
    public string TriggerReason { get; set; }

    /// <summary>
    /// Time (UTC) a run triggered for later starts at; null for a run that started right away.
    /// </summary>
    public DateTime? ScheduledAt { get; set; }

    /// <summary>
    /// Error message.
    /// </summary>
    public string Error { get; set; }

    /// <summary>
    /// Step runs with their current states.
    /// </summary>
    public List<WorkflowStepRunDto> StepRuns { get; set; } = [];

    /// <summary>
    /// Workflow step definitions (for DAG visualization).
    /// </summary>
    public List<WorkflowStepDto> Steps { get; set; } = [];

    /// <summary>
    /// Workflow edges (for DAG visualization).
    /// </summary>
    public List<WorkflowEdgeDto> Edges { get; set; } = [];
}

/// <summary>
/// DTO for a workflow step run.
/// </summary>
public class WorkflowStepRunDto
{
    /// <summary>
    /// Step run ID.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Step definition ID.
    /// </summary>
    public Guid WorkflowStepId { get; set; }

    /// <summary>
    /// Step name.
    /// </summary>
    public string StepName { get; set; }

    /// <summary>
    /// Sort order of the step within the workflow.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// Job ID.
    /// </summary>
    public Guid JobId { get; set; }

    /// <summary>
    /// Job display name.
    /// </summary>
    public string JobDisplayName { get; set; }

    /// <summary>
    /// Occurrence ID (if dispatched).
    /// </summary>
    public Guid? OccurrenceId { get; set; }

    /// <summary>
    /// Step run status.
    /// </summary>
    public WorkflowStepStatus Status { get; set; }

    /// <summary>
    /// Output data from the step.
    /// </summary>
    public string OutputData { get; set; }

    /// <summary>
    /// Error message.
    /// </summary>
    public string Error { get; set; }

    /// <summary>
    /// Start time.
    /// </summary>
    public DateTime? StartTime { get; set; }

    /// <summary>
    /// End time.
    /// </summary>
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// Duration in milliseconds.
    /// </summary>
    public long? DurationMs { get; set; }

    /// <summary>
    /// Retry count.
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Dependencies (step IDs).
    /// </summary>
    public string DependsOnStepIds { get; set; }

    /// <summary>
    /// Condition.
    /// </summary>
    public string Condition { get; set; }

    /// <summary>
    /// Delay seconds.
    /// </summary>
    public int DelaySeconds { get; set; }

    /// <summary>
    /// X position for visualization.
    /// </summary>
    public double? PositionX { get; set; }

    /// <summary>
    /// Y position for visualization.
    /// </summary>
    public double? PositionY { get; set; }
}
