using Milvaion.Application.Features.Workflows.CreateWorkflow;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Types.Structs;

namespace Milvaion.Application.Features.Workflows.UpdateWorkflow;

/// <summary>
/// Command to update an existing workflow's settings.
/// </summary>
/// <remarks>
/// Every field is wrapped in <see cref="UpdateProperty{T}"/>: only the fields sent with <c>isUpdated</c> true are
/// touched. This makes partial updates possible - renaming a workflow or toggling <see cref="IsActive"/> no
/// longer requires resending the whole definition, and concurrent editors stop clobbering each other's changes.
/// <para>
/// <see cref="Steps"/> and <see cref="Edges"/> are replaced as a single unit. Supplying one without the other is
/// rejected, because rebuilding the graph from steps alone would silently discard every edge.
/// </para>
/// </remarks>
public record UpdateWorkflowCommand : ICommand<Guid>
{
    /// <summary>
    /// ID of the workflow to update.
    /// </summary>
    public Guid WorkflowId { get; set; }

    /// <summary>
    /// Display name of the workflow.
    /// </summary>
    public UpdateProperty<string> Name { get; set; }

    /// <summary>
    /// Description of the workflow.
    /// </summary>
    public UpdateProperty<string> Description { get; set; }

    /// <summary>
    /// Tags for categorization.
    /// </summary>
    public UpdateProperty<string> Tags { get; set; }

    /// <summary>
    /// Whether this workflow is active.
    /// </summary>
    public UpdateProperty<bool> IsActive { get; set; }

    /// <summary>
    /// Failure handling strategy.
    /// </summary>
    public UpdateProperty<WorkflowFailureStrategy> FailureStrategy { get; set; }

    /// <summary>
    /// Maximum retries for failed steps.
    /// </summary>
    public UpdateProperty<int> MaxStepRetries { get; set; }

    /// <summary>
    /// Timeout in seconds for entire workflow.
    /// </summary>
    public UpdateProperty<int?> TimeoutSeconds { get; set; }

    /// <summary>
    /// Cron expression for automatic recurring execution (6-part format: second minute hour day month dayOfWeek).
    /// Null or empty means manual-only trigger.
    /// </summary>
    public UpdateProperty<string> CronExpression { get; set; }

    /// <summary>
    /// Steps of this workflow. When sent with <c>isUpdated</c> true, replaces all existing steps, and
    /// <see cref="Edges"/> must be sent as well.
    /// TempId can be an existing step's real GUID (preserved) or a temporary string (new step gets a new GUID).
    /// </summary>
    public UpdateProperty<List<CreateWorkflowStepDto>> Steps { get; set; }

    /// <summary>
    /// Edges of this workflow. When sent with <c>isUpdated</c> true, replaces all existing edges, and
    /// <see cref="Steps"/> must be sent as well.
    /// </summary>
    public UpdateProperty<List<CreateWorkflowEdgeDto>> Edges { get; set; }
}
