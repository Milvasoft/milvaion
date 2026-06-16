using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Helpers;
using Milvasoft.Interception.Ef.Transaction;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.Workflows.UpdateWorkflow;

/// <summary>
/// Handles workflow settings update.
/// </summary>
[Log]
[UserActivityTrack(UserActivity.UpdateWorkflow)]
[Transaction]
public record UpdateWorkflowCommandHandler(IMilvaionRepositoryBase<Workflow> WorkflowRepository,
                                            IMilvaionRepositoryBase<WorkflowRun> RunRepository,
                                            IMilvaionRepositoryBase<JobOccurrence> JobOccurrenceRepository,
                                            IMilvaionRepositoryBase<ScheduledJob> JobRepository) : IInterceptable, ICommandHandler<UpdateWorkflowCommand, Guid>
{
    private readonly IMilvaionRepositoryBase<Workflow> _workflowRepository = WorkflowRepository;
    private readonly IMilvaionRepositoryBase<WorkflowRun> _runRepository = RunRepository;
    private readonly IMilvaionRepositoryBase<JobOccurrence> _jobOccurrenceRepository = JobOccurrenceRepository;
    private readonly IMilvaionRepositoryBase<ScheduledJob> _jobRepository = JobRepository;

    /// <inheritdoc/>
    public async Task<Response<Guid>> Handle(UpdateWorkflowCommand request, CancellationToken cancellationToken)
    {
        var workflow = await _workflowRepository.GetByIdAsync(request.WorkflowId, cancellationToken: cancellationToken);

        if (workflow == null)
            return Response<Guid>.Error(default, "Workflow not found.");

        bool workflowDefinitionChanged = false;

        var cronChanged = workflow.CronExpression != request.CronExpression;

        WorkflowSnapshot workflowSnapshot = new()
        {
            Id = workflow.Id,
            Name = workflow.Name,
            Description = workflow.Description,
            Tags = workflow.Tags,
            IsActive = workflow.IsActive,
            FailureStrategy = workflow.FailureStrategy,
            MaxStepRetries = workflow.MaxStepRetries,
            TimeoutSeconds = workflow.TimeoutSeconds,
            Version = workflow.Version,
            CronExpression = workflow.CronExpression,
            LastScheduledRunAt = workflow.LastScheduledRunAt,
            CreationDate = workflow.CreationDate,
            CreatorUserName = workflow.CreatorUserName,
            LastModificationDate = workflow.LastModificationDate,
            LastModifierUserName = workflow.LastModifierUserName,
            Steps = [],
        };

        if (workflow.Name != request.Name || workflow.IsActive != request.IsActive || workflow.FailureStrategy != request.FailureStrategy ||
            workflow.MaxStepRetries != request.MaxStepRetries || workflow.TimeoutSeconds != request.TimeoutSeconds || cronChanged)
        {
            workflowDefinitionChanged = true;
        }

        workflow.Name = request.Name;
        workflow.Description = request.Description;
        workflow.Tags = request.Tags;
        workflow.IsActive = request.IsActive;
        workflow.FailureStrategy = request.FailureStrategy;
        workflow.MaxStepRetries = request.MaxStepRetries;
        workflow.TimeoutSeconds = request.TimeoutSeconds;
        workflow.CronExpression = string.IsNullOrWhiteSpace(request.CronExpression) ? null : request.CronExpression;
        workflow.Versions ??= [];

        // Reset last scheduled run time when cron expression changes so the engine picks up the new schedule immediately
        if (cronChanged)
            workflow.LastScheduledRunAt = null;

        if (request.Steps.Count == 0)
            return Response<Guid>.Error(default, "Workflow must have at least one step.");

        // Block step update while active runs are in progress
        var activeRuns = await _runRepository.GetAllAsync<WorkflowRun>(condition: r => r.WorkflowId == request.WorkflowId && (r.Status == WorkflowStatus.Pending || r.Status == WorkflowStatus.Running),
                                                                       projection: r => new() { Id = r.Id },
                                                                       conditionAfterProjection: null,
                                                                       tracking: false,
                                                                       splitQuery: false,
                                                                       cancellationToken: cancellationToken);

        if (!activeRuns.IsNullOrEmpty())
            return Response<Guid>.Error(default, "Cannot update steps while there are active workflow runs. Please wait for them to complete.");

        var jobIds = request.Steps.Where(s => s.NodeType == WorkflowNodeType.Task && s.JobId.HasValue).Select(s => s.JobId!.Value).Distinct().ToList();

        var existingJobIds = new HashSet<Guid>();

        var jobs = await _jobRepository.GetAllAsync(j => jobIds.Contains(j.Id), cancellationToken: cancellationToken);

        foreach (var job in jobs)
            if (job != null)
                existingJobIds.Add(job.Id);

        var missingJobs = jobIds.Except(existingJobIds).ToList();

        if (missingJobs.Count > 0)
            return Response<Guid>.Error(default, $"Jobs not found: {string.Join(", ", missingJobs)}");

        // Validate DAG (no cycles)
        if (!request.Steps.ValidateDAG(request.Edges))
            return Response<Guid>.Error(default, "Workflow contains circular dependencies. Steps must form a Directed Acyclic Graph (DAG).");

        // Get existing definition
        var existingSteps = workflow.Definition?.Steps ?? [];
        var existingEdges = workflow.Definition?.Edges ?? [];

        var existingStepIdSet = existingSteps.Select(s => s.Id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tempIdToRealId = new Dictionary<string, Guid>();

        for (int i = 0; i < request.Steps.Count; i++)
        {
            var stepCmd = request.Steps[i];
            var tempId = stepCmd.TempId ?? i.ToString();
            tempIdToRealId[tempId] = existingStepIdSet.Contains(tempId) ? Guid.Parse(tempId) : Guid.CreateVersion7();
        }

        // Build new definition
        workflow.Definition = new WorkflowDefinition
        {
            Steps = [],
            Edges = []
        };

        for (int i = 0; i < request.Steps.Count; i++)
        {
            var stepCmd = request.Steps[i];
            var tempId = stepCmd.TempId ?? i.ToString();
            var stepId = tempIdToRealId[tempId];

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

        // Delete orphaned JobOccurrences for removed steps
        var requestedStepIds = tempIdToRealId.Values.ToHashSet();
        var removedStepIds = existingSteps.Where(s => !requestedStepIds.Contains(s.Id)).Select(s => s.Id).ToList();

        if (removedStepIds.Count > 0)
            await _jobOccurrenceRepository.ExecuteDeleteAsync(o => removedStepIds.Contains(o.WorkflowStepId.Value), cancellationToken: cancellationToken);

        // Check if steps actually changed
        bool stepsActuallyChanged = existingSteps.Count != workflow.Definition.Steps.Count || existingEdges.Count != workflow.Definition.Edges.Count;

        if (!stepsActuallyChanged)
        {
            // Deep equality check
            var existingStepsDict = existingSteps.ToDictionary(s => s.Id);

            foreach (var newStep in workflow.Definition.Steps)
            {
                if (!existingStepsDict.TryGetValue(newStep.Id, out var existingStep))
                {
                    stepsActuallyChanged = true;
                    break;
                }

                if (existingStep.JobId != newStep.JobId ||
                    existingStep.NodeType != newStep.NodeType ||
                    existingStep.StepName != newStep.StepName ||
                    existingStep.Order != newStep.Order ||
                    existingStep.NodeConfigJson != newStep.NodeConfigJson ||
                    existingStep.DataMappings != newStep.DataMappings ||
                    existingStep.DelaySeconds != newStep.DelaySeconds ||
                    existingStep.JobDataOverride != newStep.JobDataOverride ||
                    existingStep.PositionX != newStep.PositionX ||
                    existingStep.PositionY != newStep.PositionY)
                {
                    stepsActuallyChanged = true;
                    break;
                }
            }
        }

        // Create version snapshot only if something actually changed
        if (workflowDefinitionChanged || stepsActuallyChanged)
        {
            // Create snapshot of current workflow before changes
            workflowSnapshot.Steps = existingSteps?.Select(s => new WorkflowStepSnapshot()
            {
                Id = s.Id,
                WorkflowId = workflow.Id,
                NodeType = s.NodeType,
                JobId = s.JobId,
                StepName = s.StepName,
                JobName = s.JobId.HasValue ? jobs.FirstOrDefault(j => j.Id == s.JobId.Value)?.DisplayName : null,
                JobVersion = s.JobId.HasValue ? jobs.FirstOrDefault(j => j.Id == s.JobId.Value)?.Version ?? 1 : 0,
                Order = s.Order,
                NodeConfigJson = s.NodeConfigJson,
                DataMappings = s.DataMappings,
                DelaySeconds = s.DelaySeconds,
                JobDataOverride = s.JobDataOverride,
                PositionX = s.PositionX,
                PositionY = s.PositionY
            }).ToList();

            workflowSnapshot.Edges = [.. existingEdges.Select(e => new WorkflowEdgeSnapshot
            {
                Id = Guid.CreateVersion7(),
                WorkflowId = workflow.Id,
                SourceStepId = e.SourceStepId,
                TargetStepId = e.TargetStepId,
                SourcePort = e.SourcePort,
                TargetPort = e.TargetPort,
                Label = e.Label,
                Order = e.Order,
                EdgeConfigJson = e.EdgeConfigJson,
            })];

            workflow.Versions.Add(workflowSnapshot);
            workflow.Version++;
        }

        // Update workflow with new JSONB definition
        await _workflowRepository.UpdateAsync(workflow, cancellationToken: cancellationToken);

        return Response<Guid>.Success(workflow.Id, "Workflow updated successfully.");
    }
}
