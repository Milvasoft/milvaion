using Milvaion.Application.Dtos.WorkflowDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.Workflows.GetWorkflowRunDetail;

/// <summary>
/// Handles the workflow run detail query.
/// </summary>
public class GetWorkflowRunDetailQueryHandler(IMilvaionRepositoryBase<WorkflowRun> runRepository,
                                               IMilvaionRepositoryBase<JobOccurrence> occurrenceRepository,
                                               IMilvaionRepositoryBase<Workflow> workflowRepository,
                                               IMilvaionRepositoryBase<ScheduledJob> jobRepository) : IInterceptable, IQueryHandler<GetWorkflowRunDetailQuery, WorkflowRunDetailDto>
{
    private readonly IMilvaionRepositoryBase<WorkflowRun> _runRepository = runRepository;
    private readonly IMilvaionRepositoryBase<JobOccurrence> _occurrenceRepository = occurrenceRepository;
    private readonly IMilvaionRepositoryBase<Workflow> _workflowRepository = workflowRepository;
    private readonly IMilvaionRepositoryBase<ScheduledJob> _jobRepository = jobRepository;

    /// <inheritdoc/>
    public async Task<Response<WorkflowRunDetailDto>> Handle(GetWorkflowRunDetailQuery request, CancellationToken cancellationToken)
    {
        var run = await _runRepository.GetByIdAsync(request.RunId, cancellationToken: cancellationToken);

        if (run == null)
            return Response<WorkflowRunDetailDto>.Success(null, "Workflow run not found.");

        var workflow = await _workflowRepository.GetByIdAsync(run.WorkflowId, projection: Workflow.Projections.Detail, cancellationToken: cancellationToken);

        var stepOccurrences = await _occurrenceRepository.GetAllAsync(condition: o => o.WorkflowRunId == run.Id, projection: o => o, cancellationToken: cancellationToken);

        var stepDtos = new List<WorkflowStepRunDto>();

        var stepIds = stepOccurrences?.Select(so => so.WorkflowStepId).Where(id => id.HasValue).Select(id => id.Value).Distinct().ToList();

        var steps = workflow?.Definition?.Steps?.Where(s => stepIds.Contains(s.Id)).ToDictionary(s => s.Id) ?? [];

        var stepJobIds = steps.Values.Where(s => s.JobId.HasValue).Select(s => s.JobId.Value).Distinct().ToList();

        // Collect all job IDs including workflow definition jobs
        var allJobIds = stepJobIds.ToHashSet();
        if (workflow?.Definition?.Steps != null)
        {
            foreach (var step in workflow.Definition.Steps.Where(s => s.JobId.HasValue))
                allJobIds.Add(step.JobId.Value);
        }

        var jobs = allJobIds.Count > 0 ? await _jobRepository.GetAllAsync(j => allJobIds.Contains(j.Id),
                                                                         projection: j => new ScheduledJob { Id = j.Id, DisplayName = j.DisplayName },
                                                                         cancellationToken: cancellationToken) : [];

        foreach (var occ in stepOccurrences ?? [])
        {
            var step = occ.WorkflowStepId.HasValue && steps.TryGetValue(occ.WorkflowStepId.Value, out var s) ? s : null;

            string jobDisplayName = "Unknown";

            if (step != null && step.JobId.HasValue)
            {
                var job = jobs.FirstOrDefault(j => j.Id == step.JobId.Value);

                jobDisplayName = job?.DisplayName ?? "Unknown";
            }

            stepDtos.Add(new WorkflowStepRunDto
            {
                Id = occ.Id,
                WorkflowStepId = occ.WorkflowStepId ?? Guid.Empty,
                StepName = step?.StepName,
                JobId = step?.JobId ?? Guid.Empty,
                JobDisplayName = jobDisplayName,
                OccurrenceId = occ.Id,
                Status = occ.StepStatus ?? WorkflowStepStatus.Pending,
                OutputData = occ.Result,
                Error = occ.Exception,
                StartTime = occ.StartTime,
                EndTime = occ.EndTime,
                DurationMs = occ.DurationMs,
                RetryCount = occ.StepRetryCount,
                DependsOnStepIds = null,
                Condition = null,
                DelaySeconds = step?.DelaySeconds ?? 0,
                Order = step?.Order ?? 0,
                PositionX = step?.PositionX,
                PositionY = step?.PositionY,
            });
        }

        stepDtos.Sort((a, b) => a.Order.CompareTo(b.Order));

        var jobIds = (workflow?.Definition?.Steps ?? []).Where(s => s.JobId.HasValue).Select(s => s.JobId!.Value).Distinct().ToList();

        var jobNames = new Dictionary<Guid, string>();

        if (jobIds.Count > 0)
            foreach (var job in jobs)
                jobNames[job.Id] = job.DisplayName;

        var dto = new WorkflowRunDetailDto
        {
            Id = run.Id,
            WorkflowId = run.WorkflowId,
            WorkflowName = workflow?.Name,
            WorkflowVersion = run.WorkflowVersion,
            CorrelationId = run.CorrelationId,
            Status = run.Status,
            StartTime = run.StartTime,
            EndTime = run.EndTime,
            DurationMs = run.DurationMs,
            TriggerReason = run.TriggerReason,
            ScheduledAt = run.ScheduledAt,
            Error = run.Error,
            StepRuns = stepDtos,
            Steps = workflow?.Definition?.Steps?.Select(s => new WorkflowStepDto
            {
                Id = s.Id,
                NodeType = s.NodeType,
                JobId = s.JobId,
                JobDisplayName = s.JobId.HasValue ? jobNames.GetValueOrDefault(s.JobId.Value, "Unknown") : null,
                StepName = s.StepName,
                Order = s.Order,
                NodeConfigJson = s.NodeConfigJson,
                PositionX = s.PositionX,
                PositionY = s.PositionY,
            }).OrderBy(s => s.Order).ToList() ?? [],
            Edges = workflow?.Definition?.Edges?.Select(e => new WorkflowEdgeDto
            {
                SourceStepId = e.SourceStepId,
                TargetStepId = e.TargetStepId,
                SourcePort = e.SourcePort,
                TargetPort = e.TargetPort,
                Label = e.Label,
                Order = e.Order,
            }).OrderBy(e => e.Order).ToList() ?? []
        };

        return Response<WorkflowRunDetailDto>.Success(dto);
    }
}
