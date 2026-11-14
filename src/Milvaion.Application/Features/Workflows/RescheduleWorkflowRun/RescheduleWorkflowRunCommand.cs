using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Features.Workflows.RescheduleWorkflowRun;

/// <summary>
/// Moves a workflow run that was triggered for later (<see cref="TriggerWorkflow.TriggerWorkflowCommand.ExecuteAt"/>) to another time.
/// Only a run that has not started yet can be moved.
/// </summary>
public record RescheduleWorkflowRunCommand : ICommand<DateTime>
{
    /// <summary>
    /// The workflow run to move.
    /// </summary>
    public Guid WorkflowRunId { get; set; }

    /// <summary>
    /// New start time (UTC). A time that has already passed starts the run right away.
    /// </summary>
    public DateTime ExecuteAt { get; set; }
}
