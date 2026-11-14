using Milvaion.Application.Dtos.WorkflowDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.Workflows.GetWorkflowDetail;

/// <summary>
/// Handles the workflow detail query.
/// </summary>
public class GetWorkflowDetailQueryHandler(IMilvaionRepositoryBase<Workflow> workflowRepository,
                                            IMilvaionRepositoryBase<ScheduledJob> jobRepository) : IInterceptable, IQueryHandler<GetWorkflowDetailQuery, WorkflowDetailDto>
{
    private readonly IMilvaionRepositoryBase<Workflow> _workflowRepository = workflowRepository;
    private readonly IMilvaionRepositoryBase<ScheduledJob> _jobRepository = jobRepository;

    /// <inheritdoc/>
    public async Task<Response<WorkflowDetailDto>> Handle(GetWorkflowDetailQuery request, CancellationToken cancellationToken)
    {
        var workflow = await _workflowRepository.GetByIdAsync(request.WorkflowId, projection: Workflow.Projections.Detail, cancellationToken: cancellationToken);

        if (workflow == null)
            return Response<WorkflowDetailDto>.Success(null, "Workflow not found");

        // Get job info for steps (display name, workerId, jobNameInWorker for schema lookup)
        var jobIds = workflow.Definition?.Steps?.Where(s => s.JobId.HasValue).Select(s => s.JobId.Value).Distinct().ToList() ?? [];
        var jobInfoById = new Dictionary<Guid, (string DisplayName, string WorkerId, string JobNameInWorker)>();

        var jobs = await _jobRepository.GetAllAsync(j => jobIds.Contains(j.Id),
                                                         projection: j => new ScheduledJob { Id = j.Id, DisplayName = j.DisplayName, WorkerId = j.WorkerId, JobNameInWorker = j.JobNameInWorker },
                                                         cancellationToken: cancellationToken);

        foreach (var job in jobs)
            jobInfoById[job.Id] = (job.DisplayName, job.WorkerId, job.JobNameInWorker);

        var dto = new WorkflowDetailDto
        {
            Id = workflow.Id,
            Name = workflow.Name,
            Description = workflow.Description,
            Tags = workflow.Tags,
            IsActive = workflow.IsActive,
            FailureStrategy = workflow.FailureStrategy,
            MaxStepRetries = workflow.MaxStepRetries,
            MaxRetryAfter = workflow.MaxRetryAfter,
            TimeoutSeconds = workflow.TimeoutSeconds,
            Version = workflow.Version,
            CronExpression = workflow.CronExpression,
            LastScheduledRunAt = workflow.LastScheduledRunAt,
            WorkflowVersions = workflow.Versions?.OrderByDescending(i => i.Version).ToList(),
            AuditInfo = new AuditDto<Guid>(workflow),
            Steps = workflow.Definition?.Steps?.Select(s =>
            {
                jobInfoById.TryGetValue(s.JobId ?? Guid.Empty, out var ji);
                return new WorkflowStepDto
                {
                    Id = s.Id,
                    NodeType = s.NodeType,
                    JobId = s.JobId,
                    JobDisplayName = ji.DisplayName,
                    WorkerId = ji.WorkerId,
                    JobNameInWorker = ji.JobNameInWorker,
                    StepName = s.StepName,
                    Order = s.Order,
                    NodeConfigJson = s.NodeConfigJson,
                    DataMappings = s.DataMappings,
                    DelaySeconds = s.DelaySeconds,
                    JobDataOverride = s.JobDataOverride,
                    PositionX = s.PositionX,
                    PositionY = s.PositionY,
                };
            }).OrderBy(s => s.Order).ToList(),
            Edges = workflow.Definition?.Edges?.Select(e => new WorkflowEdgeDto
            {
                SourceStepId = e.SourceStepId,
                TargetStepId = e.TargetStepId,
                SourcePort = e.SourcePort,
                TargetPort = e.TargetPort,
                Label = e.Label,
                Order = e.Order,
                EdgeConfigJson = e.EdgeConfigJson,
            }).OrderBy(e => e.Order).ToList() ?? []
        };

        return Response<WorkflowDetailDto>.Success(dto);
    }
}
