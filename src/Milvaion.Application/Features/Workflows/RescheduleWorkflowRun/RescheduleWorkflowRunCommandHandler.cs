using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.DataAccess.EfCore.Utils;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.Workflows.RescheduleWorkflowRun;

/// <summary>
/// Handles <see cref="RescheduleWorkflowRunCommand"/>.
/// </summary>
[Log]
[UserActivityTrack(UserActivity.RescheduleWorkflowRun)]
public record RescheduleWorkflowRunCommandHandler(IMilvaionRepositoryBase<WorkflowRun> RunRepository) : IInterceptable, ICommandHandler<RescheduleWorkflowRunCommand, DateTime>
{
    private readonly IMilvaionRepositoryBase<WorkflowRun> _runRepository = RunRepository;

    /// <inheritdoc/>
    public async Task<Response<DateTime>> Handle(RescheduleWorkflowRunCommand request, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var executeAt = request.ExecuteAt.ToUniversalTime();

        if (executeAt < now)
            executeAt = now;

        var setPropertyBuilder = new SetPropertyBuilder<WorkflowRun>().SetPropertyValue(r => r.ScheduledAt, (DateTime?)executeAt);

        // Conditional, because the engine starts the run as soon as its time comes; a started run cannot be moved.
        var affected = await _runRepository.ExecuteUpdateAsync(r => r.Id == request.WorkflowRunId
                                                                    && r.Status == WorkflowStatus.Pending
                                                                    && r.ScheduledAt != null
                                                                    && r.ScheduledAt > now,
                                                               setPropertyBuilder,
                                                               cancellationToken: cancellationToken);

        if (affected == 0)
        {
            var exists = await _runRepository.GetFirstOrDefaultAsync(condition: r => r.Id == request.WorkflowRunId, projection: r => new WorkflowRun { Id = r.Id, Status = r.Status }, cancellationToken: cancellationToken);

            return exists == null
                ? Response<DateTime>.Error(default, "Workflow run not found.")
                : Response<DateTime>.Error(default, $"Only a scheduled run that has not started yet can be rescheduled (this run is {exists.Status}).");
        }

        return Response<DateTime>.Success(executeAt, $"Workflow run rescheduled to {executeAt:O}.");
    }
}
