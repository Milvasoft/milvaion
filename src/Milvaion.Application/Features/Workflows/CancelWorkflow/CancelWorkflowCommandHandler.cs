using Microsoft.EntityFrameworkCore;
using Milvaion.Application.Interfaces.Redis;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.DataAccess.EfCore.Utils;
using Milvasoft.Interception.Ef.Transaction;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.Workflows.CancelWorkflow;

/// <summary>
/// Handles workflow cancellation.
/// </summary>
[Log]
[UserActivityTrack(UserActivity.CancelWorkflowRun)]
[Transaction]
public record CancelWorkflowCommandHandler(IMilvaionRepositoryBase<WorkflowRun> WorkflowRunRepository,
                                            IMilvaionRepositoryBase<JobOccurrence> OccurrenceRepository,
                                            IJobCancellationService CancellationService,
                                            IRedisSchedulerService RedisScheduler) : IInterceptable, ICommandHandler<CancelWorkflowCommand, bool>
{
    private readonly IMilvaionRepositoryBase<WorkflowRun> _workflowRunRepository = WorkflowRunRepository;
    private readonly IMilvaionRepositoryBase<JobOccurrence> _occurrenceRepository = OccurrenceRepository;
    private readonly IJobCancellationService _cancellationService = CancellationService;
    private readonly IRedisSchedulerService _redisScheduler = RedisScheduler;

    /// <inheritdoc/>
    public async Task<Response<bool>> Handle(CancelWorkflowCommand request, CancellationToken cancellationToken)
    {
        var run = await _workflowRunRepository.GetFirstOrDefaultAsync(condition: r => r.Id == request.WorkflowRunId, projection: q => new WorkflowRun
        {
            Id = q.Id,
            ProjectId = q.ProjectId,
            Status = q.Status,
            EndTime = q.EndTime,
            Error = q.Error,
            DurationMs = q.DurationMs,
            StartTime = q.StartTime,
            StepOccurrences = q.StepOccurrences.Select(so => new JobOccurrence
            {
                Id = so.Id,
                // The bulk update writes the row back with its project; without it the row would point at project 0.
                ProjectId = so.ProjectId,
                JobId = so.JobId,
                StepStatus = so.StepStatus,
                Status = so.Status,
                EndTime = so.EndTime,
                Exception = so.Exception
            }).ToList()
        }, cancellationToken: cancellationToken);

        if (run == null)
            return Response<bool>.Error(false, "Workflow run not found.");

        if (run.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Cancelled or WorkflowStatus.PartiallyCompleted)
            return Response<bool>.Error(false, $"Cannot cancel workflow run in {run.Status} status.");

        var now = DateTime.UtcNow;
        long? durationMs = run.StartTime.HasValue ? (long)(now - run.StartTime.Value).TotalMilliseconds : null;

        // Conditional writes only: the workflow engine and the status tracker change the same rows while this runs, and a
        // run or step that finished in the meantime must keep its outcome.
        var runSetter = new SetPropertyBuilder<WorkflowRun>().SetPropertyValue(r => r.Status, WorkflowStatus.Cancelled)
                                                             .SetPropertyValue(r => r.EndTime, (DateTime?)now)
                                                             .SetPropertyValue(r => r.Error, request.Reason)
                                                             .SetPropertyValue(r => r.DurationMs, durationMs);

        var cancelledRuns = await _workflowRunRepository.ExecuteUpdateAsync(r => r.Id == run.Id && (r.Status == WorkflowStatus.Pending || r.Status == WorkflowStatus.Running),
                                                                            runSetter,
                                                                            cancellationToken: cancellationToken);

        if (cancelledRuns == 0)
            return Response<bool>.Error(false, "The workflow run finished before it could be cancelled.");

        var runningIds = run.StepOccurrences.Where(o => o.StepStatus == WorkflowStepStatus.Running).Select(o => o.Id).ToList();
        var waitingIds = run.StepOccurrences.Where(o => o.StepStatus is WorkflowStepStatus.Pending or WorkflowStepStatus.Delayed).Select(o => o.Id).ToList();

        var cancelledSteps = 0;
        var skippedSteps = 0;

        if (runningIds.Count > 0)
        {
            var setter = new SetPropertyBuilder<JobOccurrence>().SetPropertyValue(o => o.StepStatus, (WorkflowStepStatus?)WorkflowStepStatus.Cancelled)
                                                                .SetPropertyValue(o => o.Status, JobOccurrenceStatus.Cancelled)
                                                                .SetPropertyValue(o => o.EndTime, (DateTime?)now);

            cancelledSteps = await _occurrenceRepository.ExecuteUpdateAsync(o => runningIds.Contains(o.Id)
                                                                                 && o.StepStatus == WorkflowStepStatus.Running
                                                                                 && (o.Status == JobOccurrenceStatus.Queued || o.Status == JobOccurrenceStatus.Running),
                                                                            setter,
                                                                            cancellationToken: cancellationToken);
        }

        if (waitingIds.Count > 0)
        {
            var setter = new SetPropertyBuilder<JobOccurrence>().SetPropertyValue(o => o.StepStatus, (WorkflowStepStatus?)WorkflowStepStatus.Skipped)
                                                                .SetPropertyValue(o => o.Status, JobOccurrenceStatus.Skipped)
                                                                .SetPropertyValue(o => o.EndTime, (DateTime?)now)
                                                                .SetPropertyValue(o => o.Exception, $"Step skipped due to workflow cancellation: {request.Reason}");

            skippedSteps = await _occurrenceRepository.ExecuteUpdateAsync(o => waitingIds.Contains(o.Id)
                                                                               && (o.StepStatus == WorkflowStepStatus.Pending || o.StepStatus == WorkflowStepStatus.Delayed),
                                                                          setter,
                                                                          cancellationToken: cancellationToken);
        }

        foreach (var occ in run.StepOccurrences.Where(o => runningIds.Contains(o.Id)))
        {
            try
            {
                await _cancellationService.PublishCancellationAsync(occ.Id, occ.JobId, occ.Id, request.Reason, cancellationToken);
                await _redisScheduler.MarkJobAsCompletedAsync(occ.JobId, cancellationToken);
            }
            catch
            {
                // Non-critical, continue with other cancellations
            }
        }

        return Response<bool>.Success(true, $"Workflow run cancelled. {cancelledSteps} running steps cancelled, {skippedSteps} pending steps skipped.");
    }
}
