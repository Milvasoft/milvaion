using Microsoft.EntityFrameworkCore;
using Milvaion.Application.Interfaces.Redis;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
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
            Status = q.Status,
            EndTime = q.EndTime,
            Error = q.Error,
            DurationMs = q.DurationMs,
            StartTime = q.StartTime,
            StepOccurrences = q.StepOccurrences.Select(so => new JobOccurrence
            {
                Id = so.Id,
                JobId = so.JobId,
                StepStatus = so.StepStatus,
                Status = so.Status,
                EndTime = so.EndTime,
                Exception = so.Exception
            }).ToList()
        }, tracking: true, cancellationToken: cancellationToken);

        if (run == null)
            return Response<bool>.Error(false, "Workflow run not found.");

        if (run.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Cancelled or WorkflowStatus.PartiallyCompleted)
            return Response<bool>.Error(false, $"Cannot cancel workflow run in {run.Status} status.");

        var now = DateTime.UtcNow;

        // Update workflow run status
        run.Status = WorkflowStatus.Cancelled;
        run.EndTime = now;
        run.Error = request.Reason;

        if (run.StartTime.HasValue)
            run.DurationMs = (long)(now - run.StartTime.Value).TotalMilliseconds;

        // Process step occurrences
        var runningSteps = run.StepOccurrences.Where(o => o.StepStatus == WorkflowStepStatus.Running).ToList();
        var queuedSteps = run.StepOccurrences.Where(o => o.StepStatus is WorkflowStepStatus.Pending or WorkflowStepStatus.Delayed).ToList();

        foreach (var occ in runningSteps)
        {
            occ.StepStatus = WorkflowStepStatus.Cancelled;
            occ.Status = JobOccurrenceStatus.Cancelled;
            occ.EndTime = now;

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

        // Skip queued/pending steps
        foreach (var occ in queuedSteps)
        {
            occ.StepStatus = WorkflowStepStatus.Skipped;
            occ.Status = JobOccurrenceStatus.Skipped;
            occ.EndTime = now;
            occ.Exception = $"Step skipped due to workflow cancellation: {request.Reason}";
        }

        await _workflowRunRepository.UpdateAsync(run, cancellationToken: cancellationToken, r => r.Status, r => r.StartTime, r => r.EndTime, r => r.Error, r => r.DurationMs);

        var occurrencesToUpdate = runningSteps.Concat(queuedSteps).ToList();

        if (occurrencesToUpdate.Count > 0)
        {
            await _occurrenceRepository.BulkUpdateAsync(occurrencesToUpdate, bc => bc.PropertiesToIncludeOnUpdate =
            [
                nameof(JobOccurrence.StepStatus),
                nameof(JobOccurrence.Status),
                nameof(JobOccurrence.EndTime),
                nameof(JobOccurrence.Exception)
            ], cancellationToken: cancellationToken);
        }

        return Response<bool>.Success(true, $"Workflow run cancelled. {runningSteps.Count} running steps cancelled, {queuedSteps.Count} pending steps skipped.");
    }
}
