using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.Workflows.DeleteWorkflow;

/// <summary>
/// Handles workflow deletion.
/// </summary>
[Log]
[UserActivityTrack(UserActivity.DeleteWorkflow)]
public record DeleteWorkflowCommandHandler(IMilvaionRepositoryBase<Workflow> WorkflowRepository) : IInterceptable, ICommandHandler<DeleteWorkflowCommand, Guid>
{
    private readonly IMilvaionRepositoryBase<Workflow> _workflowRepository = WorkflowRepository;

    /// <inheritdoc/>
    public async Task<Response<Guid>> Handle(DeleteWorkflowCommand request, CancellationToken cancellationToken)
    {
        var workflow = await _workflowRepository.GetForDeleteAsync(request.WorkflowId, cancellationToken: cancellationToken);

        if (workflow == null)
            return Response<Guid>.Error(default, "Workflow not found.");

        await _workflowRepository.DeleteAsync(workflow, cancellationToken: cancellationToken);

        return Response<Guid>.Success(request.WorkflowId, "Workflow deleted successfully.");
    }
}
