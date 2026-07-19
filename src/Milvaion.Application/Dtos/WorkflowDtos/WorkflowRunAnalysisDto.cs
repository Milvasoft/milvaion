using Milvasoft.Attributes.Annotations;

namespace Milvaion.Application.Dtos.WorkflowDtos;

/// <summary>
/// A workflow run flattened into a form that can be read top to bottom without joining anything.
/// </summary>
/// <remarks>
/// <see cref="WorkflowRunDetailDto"/> exists to drive the DAG canvas, so it hands back three parallel collections -
/// step runs, step definitions and edges - keyed by GUID, plus layout coordinates. A UI resolves those references
/// with a lookup table. A reader without one has to hold every GUID in mind at once to answer a question as ordinary
/// as "what stopped the pipeline", and the answer is usually wrong.
/// <para>
/// This shape resolves the references up front. Dependencies are step names rather than ids, the definition and the
/// run outcome for a step live on the same object, layout coordinates are dropped, and the collapse of the run is
/// stated directly in <see cref="FailedSteps"/> and <see cref="NotReachedSteps"/> rather than left to be inferred
/// from the absence of a record.
/// </para>
/// </remarks>
[Translate]
[ExcludeFromMetadata]
public class WorkflowRunAnalysisDto
{
    /// <summary>
    /// Workflow run ID.
    /// </summary>
    public Guid RunId { get; set; }

    /// <summary>
    /// Parent workflow ID.
    /// </summary>
    public Guid WorkflowId { get; set; }

    /// <summary>
    /// Workflow name.
    /// </summary>
    public string WorkflowName { get; set; }

    /// <summary>
    /// Version of the definition this run executed. A run always executes the version it started with, so an
    /// older run may not match the workflow as it stands now.
    /// </summary>
    public int WorkflowVersion { get; set; }

    /// <summary>
    /// Overall run status.
    /// </summary>
    public WorkflowStatus Status { get; set; }

    /// <summary>
    /// Run start time (UTC).
    /// </summary>
    public DateTime? StartTime { get; set; }

    /// <summary>
    /// Run end time (UTC).
    /// </summary>
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// Total run duration in milliseconds.
    /// </summary>
    public long? DurationMs { get; set; }

    /// <summary>
    /// Why the run started.
    /// </summary>
    public string TriggerReason { get; set; }

    /// <summary>
    /// Run level error, when the orchestrator itself failed rather than a step.
    /// </summary>
    public string Error { get; set; }

    /// <summary>
    /// One sentence describing what happened, so the headline does not have to be reconstructed from the steps.
    /// </summary>
    public string Summary { get; set; }

    /// <summary>
    /// Every step in the definition, in execution order, each carrying its own outcome.
    /// </summary>
    /// <remarks>
    /// Includes steps that never ran. A step missing from the list would be indistinguishable from a step that
    /// was never defined, and "which steps did not get to run" is the more useful half of a failed run.
    /// </remarks>
    public List<WorkflowRunStepAnalysisDto> Steps { get; set; } = [];

    /// <summary>
    /// Names of the steps that failed.
    /// </summary>
    public List<string> FailedSteps { get; set; } = [];

    /// <summary>
    /// Names of the steps that were skipped, either by a condition or because something upstream failed.
    /// </summary>
    public List<string> SkippedSteps { get; set; } = [];

    /// <summary>
    /// Names of the steps the run never reached, so they have no outcome at all.
    /// </summary>
    public List<string> NotReachedSteps { get; set; } = [];

    /// <summary>
    /// Name of the slowest completed step, which is the usual first suspect when a run is late rather than broken.
    /// </summary>
    public string SlowestStep { get; set; }
}

/// <summary>
/// One step of a workflow run, with its definition and its outcome on the same object.
/// </summary>
[Translate]
[ExcludeFromMetadata]
public class WorkflowRunStepAnalysisDto
{
    /// <summary>
    /// Step name. Unique within a workflow in practice, and used here in place of the step id.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Whether the step runs a job, branches, or joins branches.
    /// </summary>
    public WorkflowNodeType NodeType { get; set; }

    /// <summary>
    /// Display name of the scheduled job this step runs, for Task nodes.
    /// </summary>
    public string JobName { get; set; }

    /// <summary>
    /// Id of the scheduled job, so the job's own history can be looked up.
    /// </summary>
    public Guid? JobId { get; set; }

    /// <summary>
    /// Id of the occurrence this step produced, so its logs can be fetched.
    /// </summary>
    public Guid? OccurrenceId { get; set; }

    /// <summary>
    /// Step outcome. Null when the run never reached this step.
    /// </summary>
    public WorkflowStepStatus? Status { get; set; }

    /// <summary>
    /// Execution order within the workflow.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// Step start time (UTC).
    /// </summary>
    public DateTime? StartTime { get; set; }

    /// <summary>
    /// Step end time (UTC).
    /// </summary>
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// Step duration in milliseconds.
    /// </summary>
    public long? DurationMs { get; set; }

    /// <summary>
    /// How many times the step was retried before reaching its final status.
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Exception detail, when the step failed.
    /// </summary>
    public string Error { get; set; }

    /// <summary>
    /// Result the step produced, which is what downstream data mappings read from.
    /// </summary>
    public string Output { get; set; }

    /// <summary>
    /// Names of the steps that must finish before this one starts, resolved from the DAG edges.
    /// </summary>
    public List<string> DependsOn { get; set; } = [];

    /// <summary>
    /// Names of the steps waiting on this one. Reading this on a failed step gives the blast radius directly.
    /// </summary>
    public List<string> Blocks { get; set; } = [];

    /// <summary>
    /// Labels of the incoming branches, for a step reached through a condition's named port. Tells you which way
    /// a decision went without having to evaluate the expression.
    /// </summary>
    public List<string> IncomingBranches { get; set; } = [];

    /// <summary>
    /// Node configuration, holding the expression for a Condition node.
    /// </summary>
    public string NodeConfig { get; set; }

    /// <summary>
    /// Delay in seconds applied before the step runs.
    /// </summary>
    public int DelaySeconds { get; set; }
}
