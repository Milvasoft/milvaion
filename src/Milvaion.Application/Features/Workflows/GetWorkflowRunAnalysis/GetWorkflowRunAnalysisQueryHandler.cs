using Milvaion.Application.Dtos.WorkflowDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Domain.Enums;

namespace Milvaion.Application.Features.Workflows.GetWorkflowRunAnalysis;

/// <summary>
/// Handles the workflow run analysis query.
/// </summary>
/// <remarks>
/// Reads the same three sources as the detail query - the run, the definition it executed, and the occurrences it
/// produced - and joins them here instead of handing the caller the pieces to join.
/// </remarks>
public class GetWorkflowRunAnalysisQueryHandler(IMilvaionRepositoryBase<WorkflowRun> runRepository,
                                                 IMilvaionRepositoryBase<JobOccurrence> occurrenceRepository,
                                                 IMilvaionRepositoryBase<Workflow> workflowRepository,
                                                 IMilvaionRepositoryBase<ScheduledJob> jobRepository) : IInterceptable, IQueryHandler<GetWorkflowRunAnalysisQuery, WorkflowRunAnalysisDto>
{
    private readonly IMilvaionRepositoryBase<WorkflowRun> _runRepository = runRepository;
    private readonly IMilvaionRepositoryBase<JobOccurrence> _occurrenceRepository = occurrenceRepository;
    private readonly IMilvaionRepositoryBase<Workflow> _workflowRepository = workflowRepository;
    private readonly IMilvaionRepositoryBase<ScheduledJob> _jobRepository = jobRepository;

    /// <inheritdoc/>
    public async Task<Response<WorkflowRunAnalysisDto>> Handle(GetWorkflowRunAnalysisQuery request, CancellationToken cancellationToken)
    {
        var run = await _runRepository.GetByIdAsync(request.RunId, cancellationToken: cancellationToken);

        if (run == null)
            return Response<WorkflowRunAnalysisDto>.Success(null, "Workflow run not found.");

        var workflow = await _workflowRepository.GetByIdAsync(run.WorkflowId, projection: Workflow.Projections.Detail, cancellationToken: cancellationToken);

        var definitionSteps = workflow?.Definition?.Steps ?? [];
        var definitionEdges = workflow?.Definition?.Edges ?? [];

        var occurrences = await _occurrenceRepository.GetAllAsync(condition: o => o.WorkflowRunId == run.Id,
                                                                  projection: o => o,
                                                                  cancellationToken: cancellationToken);

        // One occurrence per step for the common case. A retried step can produce several, and the last one is
        // the one that decided the step's fate, so order by start time and keep the latest.
        var occurrenceByStep = (occurrences ?? [])
            .Where(o => o.WorkflowStepId.HasValue)
            .GroupBy(o => o.WorkflowStepId.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(o => o.StartTime ?? o.CreatedAt).Last());

        var jobIds = definitionSteps.Where(s => s.JobId.HasValue).Select(s => s.JobId.Value).Distinct().ToList();

        var jobNames = new Dictionary<Guid, string>();

        if (jobIds.Count > 0)
        {
            var jobs = await _jobRepository.GetAllAsync(j => jobIds.Contains(j.Id), cancellationToken: cancellationToken);

            foreach (var job in jobs ?? [])
                jobNames[job.Id] = job.DisplayName;
        }

        // Names are what the caller reads, so the id to name map is built once and every edge resolved through it.
        // A step with no name would otherwise produce dangling references, hence the fallback.
        var stepNames = definitionSteps.ToDictionary(s => s.Id, s => string.IsNullOrWhiteSpace(s.StepName)
            ? $"step-{s.Order}"
            : s.StepName);

        var steps = new List<WorkflowRunStepAnalysisDto>();

        foreach (var step in definitionSteps.OrderBy(s => s.Order))
        {
            occurrenceByStep.TryGetValue(step.Id, out var occurrence);

            var incoming = definitionEdges.Where(e => e.TargetStepId == step.Id).ToList();
            var outgoing = definitionEdges.Where(e => e.SourceStepId == step.Id).ToList();

            steps.Add(new WorkflowRunStepAnalysisDto
            {
                Name = stepNames[step.Id],
                NodeType = step.NodeType,
                JobId = step.JobId,
                JobName = step.JobId.HasValue ? jobNames.GetValueOrDefault(step.JobId.Value) : null,
                OccurrenceId = occurrence?.Id,
                // Null rather than Pending when there is no occurrence at all. Pending means the engine is holding
                // the step back; no record means the run ended before it was ever considered, and conflating the
                // two makes a halted run look like it is still going.
                Status = occurrence?.StepStatus,
                Order = step.Order,
                StartTime = occurrence?.StartTime,
                EndTime = occurrence?.EndTime,
                DurationMs = occurrence?.DurationMs,
                RetryCount = occurrence?.StepRetryCount ?? 0,
                Error = occurrence?.Exception,
                Output = Truncate(occurrence?.Result, request.MaxOutputLength),
                DependsOn = [.. incoming.Select(e => stepNames.GetValueOrDefault(e.SourceStepId)).Where(n => n is not null)],
                Blocks = [.. outgoing.Select(e => stepNames.GetValueOrDefault(e.TargetStepId)).Where(n => n is not null)],
                IncomingBranches = [.. incoming.Select(e => e.Label ?? e.SourcePort).Where(l => !string.IsNullOrWhiteSpace(l))],
                NodeConfig = step.NodeConfigJson,
                DelaySeconds = step.DelaySeconds
            });
        }

        var failed = steps.Where(s => s.Status == WorkflowStepStatus.Failed).Select(s => s.Name).ToList();
        var skipped = steps.Where(s => s.Status == WorkflowStepStatus.Skipped).Select(s => s.Name).ToList();
        var notReached = steps.Where(s => s.Status is null).Select(s => s.Name).ToList();

        var slowest = steps.Where(s => s.DurationMs.HasValue).OrderByDescending(s => s.DurationMs.Value).FirstOrDefault();

        var dto = new WorkflowRunAnalysisDto
        {
            RunId = run.Id,
            WorkflowId = run.WorkflowId,
            WorkflowName = workflow?.Name,
            WorkflowVersion = run.WorkflowVersion,
            Status = run.Status,
            StartTime = run.StartTime,
            EndTime = run.EndTime,
            DurationMs = run.DurationMs,
            TriggerReason = run.TriggerReason,
            Error = run.Error,
            Steps = steps,
            FailedSteps = failed,
            SkippedSteps = skipped,
            NotReachedSteps = notReached,
            SlowestStep = slowest?.Name,
            Summary = BuildSummary(run.Status, steps.Count, failed, skipped, notReached)
        };

        return Response<WorkflowRunAnalysisDto>.Success(dto);
    }

    /// <summary>
    /// Trims <paramref name="value"/> to <paramref name="maxLength"/>, marking it so the caller knows it is partial.
    /// </summary>
    private static string Truncate(string value, int maxLength)
    {
        if (maxLength <= 0 || string.IsNullOrEmpty(value))
            return maxLength <= 0 ? null : value;

        if (value.Length <= maxLength)
            return value;

        // Said in the value itself rather than in a sibling flag, because a truncated JSON payload otherwise
        // reads as malformed JSON and invites the wrong conclusion about the step.
        return string.Concat(value.AsSpan(0, maxLength), $"... [truncated, {value.Length} characters total]");
    }

    /// <summary>
    /// Describes the run in one sentence.
    /// </summary>
    /// <remarks>
    /// The status alone does not say much - Failed is true of a pipeline that fell over on its first step and of one
    /// that got to the last, and those call for different responses.
    /// </remarks>
    private static string BuildSummary(WorkflowStatus status, int totalSteps, List<string> failed, List<string> skipped, List<string> notReached)
    {
        var completed = totalSteps - failed.Count - skipped.Count - notReached.Count;

        return status switch
        {
            WorkflowStatus.Completed => $"All {totalSteps} steps completed successfully.",
            WorkflowStatus.Running => $"Still running. {completed} of {totalSteps} steps finished so far.",
            WorkflowStatus.Pending => "Queued, no step has started yet.",
            WorkflowStatus.Cancelled => $"Cancelled after {completed} of {totalSteps} steps.",
            WorkflowStatus.Failed when failed.Count > 0 =>
                $"Failed at {string.Join(", ", failed)}. {completed} of {totalSteps} steps completed, {notReached.Count} never ran.",
            WorkflowStatus.Failed => $"Failed without a failing step, so the orchestrator itself stopped the run. See the run level error.",
            WorkflowStatus.PartiallyCompleted =>
                $"Completed with {skipped.Count} of {totalSteps} steps skipped by a condition. Skipped is not a failure here.",
            _ => $"{completed} of {totalSteps} steps completed."
        };
    }
}
