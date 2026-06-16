using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.Workflows.CreateWorkflow;

/// <summary>
/// Handles workflow creation with DAG validation.
/// </summary>
[Log]
[UserActivityTrack(UserActivity.CreateWorkflow)]
public record CreateWorkflowCommandHandler(IMilvaionRepositoryBase<Workflow> WorkflowRepository,
                                            IMilvaionRepositoryBase<ScheduledJob> JobRepository) : IInterceptable, ICommandHandler<CreateWorkflowCommand, Guid>
{
    private readonly IMilvaionRepositoryBase<Workflow> _workflowRepository = WorkflowRepository;
    private readonly IMilvaionRepositoryBase<ScheduledJob> _jobRepository = JobRepository;

    /// <inheritdoc/>
    public async Task<Response<Guid>> Handle(CreateWorkflowCommand request, CancellationToken cancellationToken)
    {
        if (request.Steps == null || request.Steps.Count == 0)
            return Response<Guid>.Error(default, "Workflow must have at least one step.");

        // Validate all referenced jobs exist
        var jobIds = request.Steps.Where(s => s.NodeType == WorkflowNodeType.Task && s.JobId.HasValue).Select(s => s.JobId!.Value).Distinct().ToList();

        var existingJobIds = new HashSet<Guid>();

        var jobs = await _jobRepository.GetAllAsync(j => jobIds.Contains(j.Id), cancellationToken: cancellationToken);

        foreach (var jobId in jobIds)
        {
            var job = jobs.FirstOrDefault(j => j.Id == jobId);

            if (job != null)
                existingJobIds.Add(jobId);
        }

        var missingJobs = jobIds.Except(existingJobIds).ToList();

        if (missingJobs.Count > 0)
            return Response<Guid>.Error(default, $"Jobs not found: {string.Join(", ", missingJobs)}");

        var tempIdToRealId = new Dictionary<string, Guid>();

        foreach (var step in request.Steps)
            tempIdToRealId[step.TempId ?? Guid.CreateVersion7().ToString()] = Guid.CreateVersion7();

        // Validate DAG (no cycles)
        if (!request.Steps.ValidateDAG(request.Edges))
            return Response<Guid>.Error(default, "Workflow contains circular dependencies. Steps must form a Directed Acyclic Graph (DAG).");

        var workflow = new Workflow
        {
            Id = Guid.CreateVersion7(),
            Name = request.Name,
            Description = request.Description,
            Tags = request.Tags,
            IsActive = request.IsActive,
            FailureStrategy = request.FailureStrategy,
            MaxStepRetries = request.MaxStepRetries,
            TimeoutSeconds = request.TimeoutSeconds,
            CronExpression = request.CronExpression,
            Version = 1,
            Definition = new WorkflowDefinition
            {
                Steps = [],
                Edges = []
            }
        };

        var tempIds = request.Steps.Select(s => s.TempId).ToList();

        for (int i = 0; i < request.Steps.Count; i++)
        {
            var stepCmd = request.Steps[i];
            var stepId = tempIdToRealId[stepCmd.TempId ?? tempIds[i] ?? Guid.CreateVersion7().ToString()];

            workflow.Definition.Steps.Add(new WorkflowStepDefinition
            {
                Id = stepId,
                NodeType = stepCmd.NodeType,
                JobId = stepCmd.NodeType == WorkflowNodeType.Task && stepCmd.JobId.HasValue && stepCmd.JobId.Value != Guid.Empty ? stepCmd.JobId : null,
                StepName = stepCmd.StepName,
                Order = stepCmd.Order,
                NodeConfigJson = stepCmd.NodeConfigJson,
                DataMappings = stepCmd.DataMappings,
                DelaySeconds = stepCmd.DelaySeconds,
                JobDataOverride = ScheduledJob.FixJobData(stepCmd.JobDataOverride),
                PositionX = stepCmd.PositionX,
                PositionY = stepCmd.PositionY,
            });
        }

        foreach (var edgeCmd in request.Edges ?? [])
        {
            if (!tempIdToRealId.TryGetValue(edgeCmd.SourceTempId, out var sourceId) || !tempIdToRealId.TryGetValue(edgeCmd.TargetTempId, out var targetId))
                continue;

            workflow.Definition.Edges.Add(new WorkflowEdgeDefinition
            {
                SourceStepId = sourceId,
                TargetStepId = targetId,
                SourcePort = edgeCmd.SourcePort,
                TargetPort = edgeCmd.TargetPort,
                Label = edgeCmd.Label,
                Order = edgeCmd.Order,
                EdgeConfigJson = edgeCmd.EdgeConfigJson,
            });
        }

        await _workflowRepository.AddAsync(workflow, cancellationToken: cancellationToken);

        return Response<Guid>.Success(workflow.Id, "Workflow created successfully.");
    }
}
