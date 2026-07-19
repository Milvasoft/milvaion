using Milvaion.Application.Features.Workflows.CreateWorkflow;
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
/// <remarks>
/// Fields are only read when their <c>IsUpdated</c> flag is set, so a caller can rename a workflow or pause it
/// without resending the definition. The graph is only rebuilt - and the active-run guard only applies - when
/// steps and edges are actually supplied. That is what allows a running workflow to be paused, which the previous
/// full-replace shape made impossible.
/// </remarks>
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

        // Steps and edges are one unit. Rebuilding the graph from steps alone would drop every edge, so refuse
        // rather than quietly destroy the connections.
        if (request.Steps.IsUpdated != request.Edges.IsUpdated)
            return Response<Guid>.Error(default, "Steps and edges must be updated together. Send both, or neither.");

        var definitionSupplied = request.Steps.IsUpdated;

        // Snapshot of the workflow as it is now, taken before any mutation.
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

        var existingSteps = workflow.Definition?.Steps ?? [];
        var existingEdges = workflow.Definition?.Edges ?? [];

        // Description and tags are metadata: they are applied but, as before, do not on their own constitute a
        // definition change worth a new version.
        bool workflowDefinitionChanged = false;

        if (request.Name.IsUpdated)
        {
            workflowDefinitionChanged |= workflow.Name != request.Name.Value;
            workflow.Name = request.Name.Value;
        }

        if (request.Description.IsUpdated)
            workflow.Description = request.Description.Value;

        if (request.Tags.IsUpdated)
            workflow.Tags = request.Tags.Value;

        if (request.IsActive.IsUpdated)
        {
            workflowDefinitionChanged |= workflow.IsActive != request.IsActive.Value;
            workflow.IsActive = request.IsActive.Value;
        }

        if (request.FailureStrategy.IsUpdated)
        {
            workflowDefinitionChanged |= workflow.FailureStrategy != request.FailureStrategy.Value;
            workflow.FailureStrategy = request.FailureStrategy.Value;
        }

        if (request.MaxStepRetries.IsUpdated)
        {
            workflowDefinitionChanged |= workflow.MaxStepRetries != request.MaxStepRetries.Value;
            workflow.MaxStepRetries = request.MaxStepRetries.Value;
        }

        if (request.TimeoutSeconds.IsUpdated)
        {
            workflowDefinitionChanged |= workflow.TimeoutSeconds != request.TimeoutSeconds.Value;
            workflow.TimeoutSeconds = request.TimeoutSeconds.Value;
        }

        if (request.CronExpression.IsUpdated)
        {
            var newCronExpression = string.IsNullOrWhiteSpace(request.CronExpression.Value) ? null : request.CronExpression.Value;

            if (workflow.CronExpression != newCronExpression)
            {
                workflowDefinitionChanged = true;

                // Reset last scheduled run time when cron expression changes so the engine picks up the new
                // schedule immediately.
                workflow.LastScheduledRunAt = null;
            }

            workflow.CronExpression = newCronExpression;
        }

        workflow.Versions ??= [];

        // Jobs are needed both to validate a supplied definition and to label the snapshot's steps. Loading the
        // union covers a gap in the previous implementation, where a step whose job had been dropped from the new
        // definition was snapshotted without its job name.
        // Resolved once, defensively: IsUpdated true with a null list is a malformed request, not a crash.
        List<CreateWorkflowStepDto> requestedSteps = definitionSupplied ? request.Steps.Value ?? [] : [];
        List<CreateWorkflowEdgeDto> requestedEdges = definitionSupplied ? request.Edges.Value ?? [] : [];

        if (definitionSupplied && requestedSteps.Count == 0)
            return Response<Guid>.Error(default, "Workflow must have at least one step.");

        var requestJobIds = requestedSteps.Where(s => s.NodeType == WorkflowNodeType.Task && s.JobId.HasValue)
                                          .Select(s => s.JobId!.Value)
                                          .Distinct()
                                          .ToList();

        var snapshotJobIds = existingSteps.Where(s => s.JobId.HasValue).Select(s => s.JobId!.Value).Distinct().ToList();

        var allJobIds = requestJobIds.Union(snapshotJobIds).ToList();

        var jobs = allJobIds.Count > 0
            ? await _jobRepository.GetAllAsync(j => allJobIds.Contains(j.Id), cancellationToken: cancellationToken)
            : [];

        bool stepsActuallyChanged = false;

        if (definitionSupplied)
        {
            // Block definition changes while runs are in flight. Metadata-only updates deliberately skip this,
            // so a misbehaving workflow can still be paused mid-run.
            var activeRuns = await _runRepository.GetAllAsync<WorkflowRun>(condition: r => r.WorkflowId == request.WorkflowId && (r.Status == WorkflowStatus.Pending || r.Status == WorkflowStatus.Running),
                                                                          projection: r => new() { Id = r.Id },
                                                                          conditionAfterProjection: null,
                                                                          tracking: false,
                                                                          splitQuery: false,
                                                                          cancellationToken: cancellationToken);

            if (!activeRuns.IsNullOrEmpty())
                return Response<Guid>.Error(default, "Cannot update steps while there are active workflow runs. Please wait for them to complete.");

            var existingJobIds = jobs.Where(j => j != null).Select(j => j.Id).ToHashSet();

            var missingJobs = requestJobIds.Except(existingJobIds).ToList();

            if (missingJobs.Count > 0)
                return Response<Guid>.Error(default, $"Jobs not found: {string.Join(", ", missingJobs)}");

            // Validate DAG (no cycles)
            if (!requestedSteps.ValidateDAG(requestedEdges))
                return Response<Guid>.Error(default, "Workflow contains circular dependencies. Steps must form a Directed Acyclic Graph (DAG).");

            var existingStepIdSet = existingSteps.Select(s => s.Id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var tempIdToRealId = new Dictionary<string, Guid>();

            for (int i = 0; i < requestedSteps.Count; i++)
            {
                var stepCmd = requestedSteps[i];
                var tempId = stepCmd.TempId ?? i.ToString();
                tempIdToRealId[tempId] = existingStepIdSet.Contains(tempId) ? Guid.Parse(tempId) : Guid.CreateVersion7();
            }

            // Build new definition
            workflow.Definition = new WorkflowDefinition
            {
                Steps = [],
                Edges = []
            };

            for (int i = 0; i < requestedSteps.Count; i++)
            {
                var stepCmd = requestedSteps[i];
                var tempId = stepCmd.TempId ?? i.ToString();
                var stepId = tempIdToRealId[tempId];

                workflow.Definition.Steps.Add(new WorkflowStepDefinition
                {
                    Id = stepId,
                    NodeType = stepCmd.NodeType,
                    JobId = stepCmd.NodeType == WorkflowNodeType.Task && stepCmd.JobId.HasValue && stepCmd.JobId.Value != Guid.Empty ? stepCmd.JobId : null,
                    StepName = stepCmd.StepName,
                    Order = stepCmd.Order,
                    // Newly added steps arrive with temporary ids; existing ones map to themselves, so this is
                    // a no-op for anything already saved. See WorkflowStepExtensions.
                    NodeConfigJson = WorkflowStepExtensions.RemapConditionExpression(stepCmd.NodeConfigJson, tempIdToRealId),
                    DataMappings = WorkflowStepExtensions.RemapDataMappings(stepCmd.DataMappings, tempIdToRealId),
                    DelaySeconds = stepCmd.DelaySeconds,
                    JobDataOverride = ScheduledJob.FixJobData(stepCmd.JobDataOverride),
                    PositionX = stepCmd.PositionX,
                    PositionY = stepCmd.PositionY,
                });
            }

            foreach (var edgeCmd in requestedEdges)
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
            stepsActuallyChanged = existingSteps.Count != workflow.Definition.Steps.Count || existingEdges.Count != workflow.Definition.Edges.Count;

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
