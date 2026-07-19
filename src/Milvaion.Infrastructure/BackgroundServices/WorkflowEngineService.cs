using Cronos;
using EFCore.BulkExtensions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Features.Workflows.TriggerWorkflow;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Interfaces.RabbitMQ;
using Milvaion.Application.Interfaces.Redis;
using Milvaion.Infrastructure.BackgroundServices.Base;
using Milvaion.Infrastructure.Persistence.Context;
using Milvaion.Infrastructure.Telemetry;
using Milvaion.Infrastructure.Workflows;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Helpers;
using Milvasoft.Milvaion.Sdk.Utils;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Milvaion.Infrastructure.BackgroundServices;

/// <summary>
/// Background service that orchestrates workflow (DAG) execution.
/// Polls for pending/running workflow runs and dispatches ready steps.
/// </summary>
public class WorkflowEngineService(IServiceProvider serviceProvider,
                                    IOptions<WorkflowEngineOptions> options,
                                    ILoggerFactory loggerFactory,
                                    IMemoryStatsRegistry memoryStatsRegistry = null) : MemoryTrackedBackgroundService(loggerFactory, options.Value, memoryStatsRegistry)
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IRabbitMQPublisher _rabbitMQPublisher = serviceProvider.GetRequiredService<IRabbitMQPublisher>();
    private readonly IRedisSchedulerService _redisScheduler = serviceProvider.GetRequiredService<IRedisSchedulerService>();
    private readonly IRedisStatsService _redisStatsService = serviceProvider.GetRequiredService<IRedisStatsService>();
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<WorkflowEngineService>();
    private readonly WorkflowEngineOptions _options = options.Value;
    private readonly BackgroundServiceMetrics _metrics = serviceProvider.GetRequiredService<BackgroundServiceMetrics>();

    private static readonly List<string> _workflowUpdateProps = [nameof(Workflow.LastScheduledRunAt)];
    private static readonly List<string> _workflowRunUpdateProps = [nameof(WorkflowRun.Status), nameof(WorkflowRun.StartTime), nameof(WorkflowRun.EndTime), nameof(WorkflowRun.DurationMs), nameof(WorkflowRun.Error)];
    private static readonly List<string> _stepOccurrenceUpdateProps = [nameof(JobOccurrence.StepStatus), nameof(JobOccurrence.StepRetryCount), nameof(JobOccurrence.StepScheduledAt), nameof(JobOccurrence.Exception), nameof(JobOccurrence.Result), nameof(JobOccurrence.Status), nameof(JobOccurrence.EndTime), nameof(JobOccurrence.DurationMs), nameof(JobOccurrence.JobName)];
    private static readonly List<string> _stepOccurrenceDispatchProps = [nameof(JobOccurrence.Status), nameof(JobOccurrence.StepStatus), nameof(JobOccurrence.JobName), nameof(JobOccurrence.ZombieTimeoutMinutes), nameof(JobOccurrence.JobVersion), nameof(JobOccurrence.ExecutionTimeoutSeconds)];
    private static readonly List<string> _stepOccurrenceFailProps = [nameof(JobOccurrence.Status), nameof(JobOccurrence.StepStatus), nameof(JobOccurrence.Exception)];

    /// <inheritdoc/>
    protected override string ServiceName => "WorkflowEngine";

    /// <inheritdoc/>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.Debug("WorkflowEngine StartAsync called. Enabled={Enabled}", _options.Enabled);

        if (!_options.Enabled)
        {
            _logger.Warning("Workflow engine is disabled. Skipping startup.");
            return;
        }

        _logger.Information("Workflow engine service starting...");

        // Wait for database to be ready
        for (int attempt = 1; attempt <= 30; attempt++)
        {
            try
            {
                await using var scope = _serviceProvider.CreateAsyncScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

                if (await dbContext.Database.CanConnectAsync(cancellationToken))
                {
                    _logger.Debug("Database connection ready on attempt {Attempt}", attempt);
                    break;
                }
            }
            catch
            {
                _logger.Information("Database not ready yet (attempt {Attempt}/30). Waiting 2s...", attempt);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }

        _logger.Debug("Calling base.StartAsync...");
        await base.StartAsync(cancellationToken);
        _logger.Information("Workflow engine service started successfully.");
    }

    /// <inheritdoc/>
    protected override async Task ExecuteWithMemoryTrackingAsync(CancellationToken stoppingToken)
    {
        _logger.Information("Workflow engine polling started. Interval: {Interval}s", _options.PollingIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            using var _ = _metrics.MeasureDuration(ServiceName);

            try
            {
                await CheckCronWorkflowsAsync(stoppingToken);
                await ProcessWorkflowRunsAsync(stoppingToken);

                _metrics.RecordServiceIteration(ServiceName);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _metrics.RecordServiceError(ServiceName, ex.GetType().Name);
                _logger.Error(ex, "Error during workflow engine iteration");
            }
            finally
            {
                TrackMemoryAfterIteration();
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.PollingIntervalSeconds), stoppingToken);
        }
    }

    /// <inheritdoc/>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.Information("Workflow engine service stopping...");

        await base.StopAsync(cancellationToken);

        _logger.Information("Workflow engine service stopped.");
    }

    /// <summary>
    /// Checks all active workflows with a cron expression and triggers a run if the schedule is due.
    /// </summary>
    private async Task CheckCronWorkflowsAsync(CancellationToken cancellationToken)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var now = DateTime.UtcNow;

        var cronWorkflows = await dbContext.Workflows.AsNoTracking()
                                                     .Where(w => w.IsActive && w.CronExpression != null)
                                                     .Select(Workflow.Projections.CheckCron)
                                                     .ToListAsync(cancellationToken);

        var triggeredWorkflows = new List<Workflow>();

        foreach (var workflow in cronWorkflows)
        {
            try
            {
                var cronExpr = CronExpression.Parse(workflow.CronExpression, CronFormat.IncludeSeconds);

                // Use last scheduled run time as the base; fall back to slightly before now to catch first-ever runs
                var fromTime = workflow.LastScheduledRunAt ?? now.AddSeconds(-_options.PollingIntervalSeconds * 2);

                var nextOccurrence = cronExpr.GetNextOccurrence(fromTime, TimeZoneInfo.Utc);

                if (nextOccurrence.HasValue && nextOccurrence.Value <= now)
                {
                    var command = new TriggerWorkflowCommand
                    {
                        WorkflowId = workflow.Id,
                        Reason = "Cron schedule",
                        ShouldLogActivity = false,
                    };

                    await mediator.Send(command, cancellationToken);

                    workflow.LastScheduledRunAt = now;

                    triggeredWorkflows.Add(workflow);

                    _logger.Debug("Workflow {WorkflowId} triggered by cron schedule ({CronExpression})", workflow.Id, workflow.CronExpression);
                }
            }
            catch (Exception ex)
            {
                _metrics.RecordServiceError(ServiceName, "Cron_Schedule_Check_Failed");
                _logger.Error(ex, "Error checking cron schedule for workflow {WorkflowId}", workflow.Id);
            }
        }

        if (triggeredWorkflows.Count > 0)
            await dbContext.BulkUpdateAsync(triggeredWorkflows, bc => bc.PropertiesToIncludeOnUpdate = _workflowUpdateProps, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Processes all active workflow runs.
    /// </summary>
    private async Task<int> ProcessWorkflowRunsAsync(CancellationToken cancellationToken)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

        var activeRuns = await dbContext.WorkflowRuns.AsNoTracking()
                                        .Include(r => r.StepOccurrences)
                                        .Include(r => r.Workflow)
                                        .Where(r => r.Status == WorkflowStatus.Pending || r.Status == WorkflowStatus.Running)
                                        .ToListAsync(cancellationToken);

        if (activeRuns.IsNullOrEmpty())
            return 0;

        _logger.Debug("Processing {Count} active workflow runs", activeRuns.Count);

        var now = DateTime.UtcNow;

        // Process all runs in memory — collect all changes without touching the DB
        var runsToUpdate = new List<WorkflowRun>();
        var occurrencesToUpdate = new List<JobOccurrence>();
        var pendingDispatches = new List<(WorkflowRun Run, JobOccurrence Occurrence, WorkflowStepDefinition StepDef)>();
        var runningOccurrencesToCancel = new List<JobOccurrence>();

        foreach (var run in activeRuns)
        {
            try
            {
                DetectRunChanges(run, now, runsToUpdate, occurrencesToUpdate, pendingDispatches, runningOccurrencesToCancel);
            }
            catch (Exception ex)
            {
                _metrics.RecordServiceError(ServiceName, "Workflow_Run_Processing_Failed");
                _logger.Error(ex, "Error processing workflow run {RunId}", run.Id);
            }
        }

        // Load all jobs needed for dispatch and for naming non-dispatched occurrences (skipped, cancelled, delayed)
        var jobIds = pendingDispatches.Where(d => d.StepDef.JobId.HasValue)
                                      .Select(d => d.StepDef.JobId!.Value)
                                      .Concat(occurrencesToUpdate.Where(o => string.IsNullOrWhiteSpace(o.JobName) && o.WorkflowStepId.HasValue && o.JobId != Guid.Empty)
                                                                 .Select(o => o.JobId))
                                      .Distinct()
                                      .ToList();

        var jobsById = jobIds.Count > 0 ? await dbContext.ScheduledJobs.AsNoTracking().Where(j => jobIds.Contains(j.Id)).ToDictionaryAsync(j => j.Id, cancellationToken) : [];

        // Populate JobName for occurrences that were never dispatched (skipped, cancelled, delayed)
        foreach (var occ in occurrencesToUpdate.Where(o => string.IsNullOrWhiteSpace(o.JobName) && o.WorkflowStepId.HasValue))
        {
            if (jobsById.TryGetValue(occ.JobId, out var nameJob))
                occ.JobName = nameJob.JobNameInWorker;
        }

        // Resolve job data for each dispatch; mark as failed if job not found
        var validDispatches = new List<(WorkflowRun Run, JobOccurrence Occurrence, WorkflowStepDefinition StepDef, ScheduledJob Job, string JobData)>();

        foreach (var (run, occurrence, stepDef) in pendingDispatches)
        {
            if (stepDef.NodeType != WorkflowNodeType.Task || !stepDef.JobId.HasValue)
                continue;

            if (!jobsById.TryGetValue(stepDef.JobId.Value, out var job))
            {
                occurrence.StepStatus = WorkflowStepStatus.Failed;
                occurrence.Exception = $"Job {stepDef.JobId} not found.";
                occurrencesToUpdate.Add(occurrence);
                continue;
            }

            // Merge priority (lowest → highest):
            // 1. job.JobData (design-time base)
            // 2. stepDef.JobDataOverride (design-time per-step override)
            // 3. run.StepJobData[stepId] (run-time per-step override, highest priority)
            var jobData = MergeJobData(job.JobData, stepDef.JobDataOverride);

            if (run.StepJobData != null && run.StepJobData.TryGetValue(stepDef.Id, out var runtimeStepData))
                jobData = MergeJobData(jobData, runtimeStepData);

            var occurrencesDict = run.StepOccurrences.ToDictionary(o => o.WorkflowStepId!.Value);

            if (!string.IsNullOrWhiteSpace(stepDef.DataMappings))
                jobData = ApplyDataMappings(jobData, stepDef.DataMappings, occurrencesDict);

            validDispatches.Add((run, occurrence, stepDef, job, jobData));
        }

        // Single bulk save for all run/step status changes
        if (runsToUpdate.Count > 0)
            await dbContext.BulkUpdateAsync(runsToUpdate, bc => bc.PropertiesToIncludeOnUpdate = _workflowRunUpdateProps, cancellationToken: cancellationToken);

        if (occurrencesToUpdate.Count > 0)
            await dbContext.BulkUpdateAsync(occurrencesToUpdate, bc => bc.PropertiesToIncludeOnUpdate = _stepOccurrenceUpdateProps, cancellationToken: cancellationToken);

        // Batch dispatch all ready steps
        if (validDispatches.Count > 0)
        {
            var eventPublisher = scope.ServiceProvider.GetService<IJobOccurrenceEventPublisher>();
            await DispatchStepsAsync(dbContext, eventPublisher, validDispatches, cancellationToken);
        }

        // Send Redis cancellation signals for Running steps that were cancelled due to workflow failure/timeout
        if (runningOccurrencesToCancel.Count > 0)
        {
            var cancellationService = scope.ServiceProvider.GetService<IJobCancellationService>();

            if (cancellationService != null)
            {
                foreach (var occ in runningOccurrencesToCancel)
                {
                    try
                    {
                        await cancellationService.PublishCancellationAsync(occ.Id, occ.JobId, occ.Id, "Workflow stopped", cancellationToken);
                        await _redisScheduler.MarkJobAsCompletedAsync(occ.JobId, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Failed to send cancellation signal for workflow step occurrence {OccurrenceId}", occ.Id);
                    }
                }
            }
        }

        return activeRuns.Count; // Return count for adaptive polling
    }

    /// <summary>
    /// Processes a single workflow run in memory: mutates run/step state and populates change lists.
    /// No database calls — all persistence is batched at the caller level.
    /// </summary>
    private void DetectRunChanges(WorkflowRun run,
                                  DateTime now,
                                  List<WorkflowRun> runsToUpdate,
                                  List<JobOccurrence> occurrencesToUpdate,
                                  List<(WorkflowRun Run, JobOccurrence Occurrence, WorkflowStepDefinition StepDef)> pendingDispatches,
                                  List<JobOccurrence> runningOccurrencesToCancel)
    {
        // Start pending run
        if (run.Status == WorkflowStatus.Pending)
        {
            run.Status = WorkflowStatus.Running;
            run.StartTime = now;
            runsToUpdate.Add(run);
        }

        // Check for timeout
        if (run.Workflow?.TimeoutSeconds > 0 && run.StartTime.HasValue && (now - run.StartTime.Value).TotalSeconds > run.Workflow.TimeoutSeconds)
        {
            CancelRun(run, now, "Workflow timed out", runsToUpdate, occurrencesToUpdate, runningOccurrencesToCancel);
            return;
        }

        var stepDefinitions = run.Workflow?.Definition?.Steps?.ToDictionary(s => s.Id) ?? [];
        var workflowEdges = run.Workflow?.Definition?.Edges ?? [];
        var stepOccurrences = run.StepOccurrences.ToDictionary(o => o.WorkflowStepId!.Value);

        // Track virtual node states in-memory
        var virtualNodeStates = stepDefinitions.Values.Where(s => s.NodeType != WorkflowNodeType.Task && !stepOccurrences.ContainsKey(s.Id))
                                                      .ToDictionary(s => s.Id, s => WorkflowStepStatus.Pending);

        // Track condition node results for port-based activation
        var conditionResults = new Dictionary<Guid, string>();

        // Check for failures
        var failedOccs = run.StepOccurrences.Where(o => o.StepStatus == WorkflowStepStatus.Failed).ToList();

        if (failedOccs.Count > 0)
        {
            var failureStrategy = run.Workflow?.FailureStrategy ?? WorkflowFailureStrategy.StopOnFirstFailure;

            if (failureStrategy == WorkflowFailureStrategy.StopOnFirstFailure)
            {
                var maxRetries = run.Workflow?.MaxStepRetries ?? 0;
                var exhaustedOccs = failedOccs.Where(o => o.StepRetryCount >= maxRetries).ToList();

                if (exhaustedOccs.Count > 0 || maxRetries == 0)
                {
                    FailRun(run, now, $"Step(s) failed: {string.Join(", ", exhaustedOccs.Select(o => stepDefinitions.GetValueOrDefault(o.WorkflowStepId!.Value)?.StepName ?? o.WorkflowStepId.ToString()))}", runsToUpdate, occurrencesToUpdate, runningOccurrencesToCancel);
                    return;
                }

                var occsToRetry = failedOccs.Where(o => o.StepRetryCount < maxRetries).ToList();

                foreach (var occ in occsToRetry)
                {
                    occ.StepStatus = WorkflowStepStatus.Pending;
                    occ.StepRetryCount++;
                    occ.Exception = null;
                    occ.Result = null;
                    occ.Status = JobOccurrenceStatus.Queued;
                    occ.EndTime = null;
                    occ.DurationMs = null;
                }

                occurrencesToUpdate.AddRange(occsToRetry);
            }
        }

        // Process virtual nodes first so their results are available for downstream task steps
        foreach (var kvp in virtualNodeStates.Where(kv => kv.Value == WorkflowStepStatus.Pending).ToList())
        {
            var stepId = kvp.Key;
            var stepDef = stepDefinitions[stepId];

            if (!AreDependenciesSatisfied(stepDef, workflowEdges, stepOccurrences, virtualNodeStates, run.Workflow?.FailureStrategy ?? WorkflowFailureStrategy.StopOnFirstFailure, conditionResults, _logger))
                continue;

            // Condition nodes: evaluate expression
            if (stepDef.NodeType == WorkflowNodeType.Condition)
            {
                var result = EvaluateCondition(stepDef, workflowEdges, stepOccurrences);

                virtualNodeStates[stepId] = WorkflowStepStatus.Completed;
                conditionResults[stepId] = result ? "true" : "false";

                _logger.Debug("Condition node {StepId} ({StepName}) evaluated to {Result}", stepId, stepDef.StepName, conditionResults[stepId]);
                continue;
            }

            // Merge: instant complete
            if (stepDef.NodeType == WorkflowNodeType.Merge)
            {
                virtualNodeStates[stepId] = WorkflowStepStatus.Completed;
                continue;
            }
        }

        // Find steps that are ready to execute
        var stepsToSkipDueToPort = new List<JobOccurrence>();

        foreach (var occ in run.StepOccurrences.Where(o => o.StepStatus == WorkflowStepStatus.Pending))
        {
            var stepDef = stepDefinitions.GetValueOrDefault(occ.WorkflowStepId!.Value);

            if (stepDef == null)
                continue;

            // Check if this step should be skipped due to port-based branching
            if (ShouldSkipDueToPortMismatch(stepDef, workflowEdges, stepOccurrences, virtualNodeStates, conditionResults, stepDefinitions, out var skipReason))
            {
                occ.StepStatus = WorkflowStepStatus.Skipped;
                occ.Status = JobOccurrenceStatus.Skipped;
                occ.EndTime = now;
                occ.Exception = skipReason;
                stepsToSkipDueToPort.Add(occ);

                _logger.Debug("Step {StepId} ({StepName}) skipped: {Reason}", stepDef.Id, stepDef.StepName, skipReason);
                continue;
            }

            if (!AreDependenciesSatisfied(stepDef, workflowEdges, stepOccurrences, virtualNodeStates, run.Workflow?.FailureStrategy ?? WorkflowFailureStrategy.StopOnFirstFailure, conditionResults, _logger))
                continue;

            if (stepDef.DelaySeconds > 0 && occ.StepScheduledAt == null)
            {
                occ.StepStatus = WorkflowStepStatus.Delayed;
                occ.StepScheduledAt = now.AddSeconds(stepDef.DelaySeconds);
                occurrencesToUpdate.Add(occ);
                continue;
            }

            pendingDispatches.Add((run, occ, stepDef));
        }

        occurrencesToUpdate.AddRange(stepsToSkipDueToPort);

        // Also check delayed steps that are now due
        foreach (var occ in run.StepOccurrences.Where(o => o.StepStatus == WorkflowStepStatus.Delayed && o.StepScheduledAt <= now))
        {
            var stepDef = stepDefinitions.GetValueOrDefault(occ.WorkflowStepId!.Value);

            if (stepDef != null)
                pendingDispatches.Add((run, occ, stepDef));
        }

        // Check completion
        var allTasksComplete = run.StepOccurrences.All(o => o.StepStatus is WorkflowStepStatus.Completed or WorkflowStepStatus.Failed or WorkflowStepStatus.Skipped or WorkflowStepStatus.Cancelled);
        var allVirtualNodesComplete = virtualNodeStates.Values.All(s => s != WorkflowStepStatus.Pending);

        if (allTasksComplete && allVirtualNodesComplete)
        {
            var hasFailures = run.StepOccurrences.Any(o => o.StepStatus == WorkflowStepStatus.Failed);
            var hasSkipped = run.StepOccurrences.Any(o => o.StepStatus == WorkflowStepStatus.Skipped);
            var hasCompleted = run.StepOccurrences.Any(o => o.StepStatus == WorkflowStepStatus.Completed);

            // Failures with no completions → Failed; failures alongside completions/skips → PartiallyCompleted
            run.Status = hasFailures && !hasCompleted
                ? WorkflowStatus.Failed
                : hasFailures || hasSkipped
                    ? WorkflowStatus.PartiallyCompleted
                    : WorkflowStatus.Completed;

            if (hasFailures)
                run.Error = "One or more steps failed.";

            run.EndTime = now;

            if (run.StartTime.HasValue)
                run.DurationMs = (long)(now - run.StartTime.Value).TotalMilliseconds;

            runsToUpdate.Add(run);

            _logger.Information("Workflow run {RunId} completed with status {Status}", run.Id, run.Status);
        }
    }

    /// <summary>
    /// Checks if step should be skipped due to port-based branching.
    /// If all incoming edges with port requirements have mismatched source results, step is skipped.
    /// </summary>
    private static bool ShouldSkipDueToPortMismatch(WorkflowStepDefinition stepDef,
                                                    List<WorkflowEdgeDefinition> workflowEdges,
                                                    Dictionary<Guid, JobOccurrence> stepOccurrences,
                                                    Dictionary<Guid, WorkflowStepStatus> virtualNodeStates,
                                                    Dictionary<Guid, string> conditionResults,
                                                    Dictionary<Guid, WorkflowStepDefinition> stepDefinitions,
                                                    out string skipReason)
    {
        skipReason = null;
        var incomingEdges = workflowEdges.Where(e => e.TargetStepId == stepDef.Id).ToList();

        // Root step can't be skipped
        if (incomingEdges.Count == 0)
            return false;

        var portedEdges = incomingEdges.Where(e => !string.IsNullOrWhiteSpace(e.SourcePort)).ToList();

        // No port requirements
        if (portedEdges.Count == 0)
            return false;

        // If all ported edges have sources that are complete but with mismatched port results, skip this step
        var allPortMismatches = true;

        foreach (var edge in portedEdges)
        {
            // Check source node type - Merge nodes don't produce port results
            if (stepDefinitions.TryGetValue(edge.SourceStepId, out var sourceStepDef))
            {
                // Merge nodes always pass through, ignore port
                if (sourceStepDef.NodeType == WorkflowNodeType.Merge)
                {
                    allPortMismatches = false;
                    break;
                }
            }

            WorkflowStepStatus? sourceStatus = null;
            string sourceResult = null;

            if (stepOccurrences.TryGetValue(edge.SourceStepId, out var depOcc))
            {
                sourceStatus = depOcc.StepStatus;
                sourceResult = depOcc.Result;
            }
            else if (virtualNodeStates.TryGetValue(edge.SourceStepId, out var virtualStatus))
            {
                sourceStatus = virtualStatus;
                sourceResult = conditionResults?.GetValueOrDefault(edge.SourceStepId);
            }

            // If source not complete yet, we can't determine skip
            if (sourceStatus != WorkflowStepStatus.Completed)
            {
                allPortMismatches = false;
                break;
            }

            // Check if port matches (only for Condition nodes)
            var port = edge.SourcePort.Trim();
            var matches = (port.Equals("true", StringComparison.OrdinalIgnoreCase) && sourceResult == "true") ||
                          (port.Equals("false", StringComparison.OrdinalIgnoreCase) && sourceResult == "false");

            // At least one edge matches, step should execute
            if (matches)
            {
                allPortMismatches = false;
                break;
            }
        }

        if (allPortMismatches)
        {
            skipReason = "All incoming condition branches evaluated to opposite path";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if all incoming edges are satisfied (source nodes completed/skipped with matching port).
    /// </summary>
    private static bool AreDependenciesSatisfied(WorkflowStepDefinition stepDef,
                                                 List<WorkflowEdgeDefinition> workflowEdges,
                                                 Dictionary<Guid, JobOccurrence> stepOccurrences,
                                                 Dictionary<Guid, WorkflowStepStatus> virtualNodeStates,
                                                 WorkflowFailureStrategy failureStrategy,
                                                 Dictionary<Guid, string> conditionResults,
                                                 IMilvaLogger logger)
    {
        var incomingEdges = workflowEdges.Where(e => e.TargetStepId == stepDef.Id).ToList();

        // Root step
        if (incomingEdges.Count == 0)
            return true;

        foreach (var edge in incomingEdges)
        {
            // Check if source is task node (has occurrence) or virtual node (in-memory state)
            WorkflowStepStatus? sourceStatus = null;

            string sourceResult = null;

            if (stepOccurrences.TryGetValue(edge.SourceStepId, out var depOcc))
            {
                sourceStatus = depOcc.StepStatus;
                sourceResult = depOcc.Result;
            }
            else if (virtualNodeStates.TryGetValue(edge.SourceStepId, out var virtualStatus))
            {
                sourceStatus = virtualStatus;
                sourceResult = conditionResults?.GetValueOrDefault(edge.SourceStepId);
            }

            if (!sourceStatus.HasValue)
                return false;

            // Check port-based activation for condition branches
            if (!string.IsNullOrWhiteSpace(edge.SourcePort) && sourceStatus == WorkflowStepStatus.Completed)
            {
                var port = edge.SourcePort.Trim();

                if (port.Equals("true", StringComparison.OrdinalIgnoreCase) && sourceResult != "true")
                {
                    logger?.Debug("Step {StepId} edge from {SourceId} not activated: port '{Port}' but got '{Result}'", stepDef.Id, edge.SourceStepId, port, sourceResult);
                    continue;
                }

                if (port.Equals("false", StringComparison.OrdinalIgnoreCase) && sourceResult != "false")
                {
                    logger?.Debug("Step {StepId} edge from {SourceId} not activated: port '{Port}' but got '{Result}'", stepDef.Id, edge.SourceStepId, port, sourceResult);
                    continue;
                }

                continue;
            }

            if (sourceStatus is WorkflowStepStatus.Completed or WorkflowStepStatus.Skipped)
                continue;

            if (failureStrategy == WorkflowFailureStrategy.ContinueOnFailure && sourceStatus == WorkflowStepStatus.Failed)
                continue;

            return false;
        }

        return true;
    }

    /// <summary>
    /// Extracts the condition expression from node config JSON.
    /// </summary>
    /// <remarks>
    /// The expression grammar itself lives in <see cref="ConditionExpression"/>. A clause is
    /// <c>[stepId:](@status | $.field) &lt;operator&gt; &lt;value&gt;</c>, and clauses combine with
    /// <c>&amp;&amp;</c>, <c>||</c> and parentheses.
    /// </remarks>
    private static string ExtractConditionExpression(string nodeConfigJson)
    {
        if (string.IsNullOrWhiteSpace(nodeConfigJson))
            return null;

        try
        {
            var json = JsonNode.Parse(nodeConfigJson);
            return json?["expression"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Evaluates whether the specified workflow step should execute based on its condition expression and the states of
    /// its dependent steps.
    /// </summary>
    /// <remarks>If the condition expression is empty, missing, or an error occurs during evaluation, the
    /// method defaults to allowing the step to execute. The evaluation logic supports logical AND and OR
    /// operators to combine conditions on dependent steps.</remarks>
    /// <param name="stepDef">The workflow step definition containing the condition expression to evaluate.</param>
    /// <param name="workflowEdges">A list of workflow edges representing dependencies between workflow steps.</param>
    /// <param name="stepOccurrences">A dictionary mapping step identifiers to their corresponding job occurrences, used to determine the state of
    /// each dependent step.</param>
    /// <returns>true if the condition expression evaluates to allow execution of the step; otherwise, false.</returns>
    private static bool EvaluateCondition(WorkflowStepDefinition stepDef, List<WorkflowEdgeDefinition> workflowEdges, Dictionary<Guid, JobOccurrence> stepOccurrences)
    {
        try
        {
            var condition = ExtractConditionExpression(stepDef.NodeConfigJson);

            if (string.IsNullOrWhiteSpace(condition))
                return true;

            var allDepIds = workflowEdges.Where(e => e.TargetStepId == stepDef.Id)
                                         .Select(e => e.SourceStepId)
                                         .Distinct()
                                         .ToList();

            if (allDepIds.Count == 0)
                return true;

            return ConditionExpression.Evaluate(condition, clause => EvaluateClause(clause, allDepIds, stepOccurrences));
        }
        catch
        {
            // On error, default to executing
            return true;
        }
    }

    /// <summary>
    /// Evaluates a single clause (no and/or operators).
    /// Format: [stepId:](@status|$.field) operator value
    /// </summary>
    private static bool EvaluateClause(string clause, List<Guid> allDepIds, Dictionary<Guid, JobOccurrence> stepOccurrences)
    {
        try
        {
            List<Guid> targetIds;
            string expression;

            var colonIdx = clause.IndexOf(':');

            if (colonIdx > 0 && Guid.TryParse(clause[..colonIdx], out var specificId))
            {
                targetIds = [specificId];
                expression = clause[(colonIdx + 1)..].Trim();
            }
            else
            {
                targetIds = allDepIds;
                expression = clause;
            }

            // @status — checks WorkflowStepStatus of target parent(s); All must satisfy
            if (expression.StartsWith("@status"))
            {
                var opAndValue = expression[7..].Trim();
                var op = opAndValue.StartsWith("!=") ? "!=" : "==";
                var expectedValue = opAndValue[op.Length..].Trim().Trim('\'', '"');

                foreach (var depId in targetIds)
                {
                    if (!stepOccurrences.TryGetValue(depId, out var depOcc))
                        return false;

                    var statusStr = depOcc.StepStatus.ToString();

                    if (op == "==" && !string.Equals(statusStr, expectedValue, StringComparison.OrdinalIgnoreCase))
                        return false;

                    if (op == "!=" && string.Equals(statusStr, expectedValue, StringComparison.OrdinalIgnoreCase))
                        return false;
                }

                return true;
            }

            // $.field — checks result JSON of target parent(s); ALL parents that have a result must satisfy
            if (expression.StartsWith("$."))
            {
                string matchedOp = null;
                string[] parts = null;

                foreach (var candidate in (ReadOnlySpan<string>)[" >= ", " <= ", " == ", " != ", " > ", " < "])
                {
                    parts = expression[2..].Split([candidate], 2, StringSplitOptions.TrimEntries);

                    if (parts.Length == 2)
                    {
                        matchedOp = candidate.Trim();
                        break;
                    }
                }

                if (matchedOp == null)
                    return true;

                var fieldPath = parts[0];
                var expectedValue = parts[1].Trim('\'', '"');

                foreach (var depId in targetIds)
                {
                    if (!stepOccurrences.TryGetValue(depId, out var depOcc) || string.IsNullOrWhiteSpace(depOcc.Result))
                        continue;

                    try
                    {
                        var json = JsonNode.Parse(depOcc.Result);

                        if (json == null)
                            continue;

                        if (!CompareValues(GetJsonValue(json, fieldPath)?.ToString(), expectedValue, matchedOp))
                            return false;
                    }
                    catch (JsonException) { }
                }

                return true;
            }

            // Unknown format → default execute
            return true;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Compares actual and expected values based on the operator. Supports string equality and numeric comparisons.
    /// </summary>
    /// <param name="actual"></param>
    /// <param name="expected"></param>
    /// <param name="op"></param>
    /// <returns></returns>
    private static bool CompareValues(string actual, string expected, string op) => op switch
    {
        "==" => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
        "!=" => !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
        ">" when double.TryParse(actual, out var a) && double.TryParse(expected, out var e) => a > e,
        "<" when double.TryParse(actual, out var a) && double.TryParse(expected, out var e) => a < e,
        ">=" when double.TryParse(actual, out var a) && double.TryParse(expected, out var e) => a >= e,
        "<=" when double.TryParse(actual, out var a) && double.TryParse(expected, out var e) => a <= e,
        _ => true
    };

    /// <summary>
    /// Updates all pre-created step occurrences to Queued/Running, bulk-inserts dispatch logs,
    /// then publishes to RabbitMQ. Failures are bulk-saved in a single pass.
    /// </summary>
    private async Task DispatchStepsAsync(MilvaionDbContext dbContext,
                                          IJobOccurrenceEventPublisher eventPublisher,
                                          List<(WorkflowRun Run, JobOccurrence Occurrence, WorkflowStepDefinition StepDef, ScheduledJob Job, string JobData)> dispatches,
                                          CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var logs = new List<JobOccurrenceLog>(dispatches.Count);

        foreach (var (run, occ, stepDef, job, _) in dispatches)
        {
            occ.Status = JobOccurrenceStatus.Queued;
            occ.StepStatus = WorkflowStepStatus.Running;
            occ.JobName = job.JobNameInWorker;
            occ.ZombieTimeoutMinutes = job.ZombieTimeoutMinutes;
            occ.JobVersion = job.Version;
            occ.ExecutionTimeoutSeconds = job.ExecutionTimeoutSeconds;

            logs.Add(new JobOccurrenceLog
            {
                Id = Guid.CreateVersion7(),
                OccurrenceId = occ.Id,
                Timestamp = now,
                Level = "Information",
                Message = $"Workflow step '{stepDef.StepName}' dispatched (Run: {run.Id})",
                Category = "WorkflowEngine",
                Data = new Dictionary<string, object>
                {
                    ["WorkflowRunId"] = run.Id.ToString(),
                    ["WorkflowStepId"] = stepDef.Id.ToString(),
                    ["StepName"] = stepDef.StepName,
                    ["RetryCount"] = occ.StepRetryCount,
                }
            });
        }

        // Atomically persist all occurrence status changes and logs before any RabbitMQ publish
        await dbContext.BulkUpdateAsync(dispatches.Select(d => d.Occurrence).ToList(), bc => bc.PropertiesToIncludeOnUpdate = _stepOccurrenceDispatchProps, cancellationToken: cancellationToken);
        await dbContext.BulkInsertAsync(logs, cancellationToken: cancellationToken);

        // Publish to RabbitMQ and collect results
        var failedOccurrences = new List<JobOccurrence>();
        var succeededOccurrences = new List<JobOccurrence>();

        for (var i = 0; i < dispatches.Count; i++)
        {
            var (run, occ, stepDef, job, jobData) = dispatches[i];

            var jobForDispatch = new ScheduledJob
            {
                Id = job.Id,
                JobNameInWorker = job.JobNameInWorker,
                JobData = jobData,
                WorkerId = job.WorkerId,
                RoutingPattern = job.RoutingPattern,
                ZombieTimeoutMinutes = job.ZombieTimeoutMinutes,
                ExecutionTimeoutSeconds = job.ExecutionTimeoutSeconds,
                Version = job.Version,
                DisplayName = job.DisplayName,
                CreatorUserName = "WorkflowEngine",
            };

            var published = await _rabbitMQPublisher.PublishJobAsync(jobForDispatch, occ.Id, cancellationToken);

            if (!published)
            {
                occ.StepStatus = WorkflowStepStatus.Failed;
                occ.Exception = "Failed to publish to RabbitMQ";
                occ.Status = JobOccurrenceStatus.Failed;
                failedOccurrences.Add(occ);
            }
            else
            {
                succeededOccurrences.Add(occ);
                _logger.Debug("Dispatched workflow step '{StepName}' for run {RunId}, occurrence {OccurrenceId}", stepDef.StepName, run.Id, occ.Id);
            }
        }

        if (failedOccurrences.Count > 0)
            await dbContext.BulkUpdateAsync(failedOccurrences, bc => bc.PropertiesToIncludeOnUpdate = _stepOccurrenceFailProps, cancellationToken: cancellationToken);

        if (succeededOccurrences.Count > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _redisStatsService.IncrementTotalOccurrencesAsync(succeededOccurrences.Count, cancellationToken);
                    await _redisStatsService.IncrementStatusCounterAsync(JobOccurrenceStatus.Queued, succeededOccurrences.Count, cancellationToken);
                }
                catch
                {
                    // Non-critical
                }
            }, CancellationToken.None);

            if (eventPublisher != null)
                await eventPublisher.PublishOccurrenceCreatedAsync(succeededOccurrences, _logger, cancellationToken);
        }
    }

    /// <summary>
    /// Merges <paramref name="globalJobData"/> on top of <paramref name="baseJobData"/>.
    /// Properties in <paramref name="globalJobData"/> overwrite matching keys in the base.
    /// If <paramref name="globalJobData"/> is null/empty, <paramref name="baseJobData"/> is returned as-is.
    /// </summary>
    private static string MergeJobData(string baseJobData, string globalJobData)
    {
        if (string.IsNullOrWhiteSpace(globalJobData))
            return baseJobData;

        try
        {
            var target = !string.IsNullOrWhiteSpace(baseJobData) ? JsonNode.Parse(baseJobData)?.AsObject() : [];
            target ??= [];

            var overlay = JsonNode.Parse(globalJobData)?.AsObject();

            if (overlay != null)
            {
                foreach (var prop in overlay)
                    target[prop.Key] = prop.Value?.DeepClone();
            }

            return target.ToJsonString();
        }
        catch
        {
            return baseJobData;
        }
    }

    /// <summary>
    /// Applies data mappings from parent step outputs to the current step's job data.
    /// </summary>
    private static string ApplyDataMappings(string jobData, string dataMappingsJson, Dictionary<Guid, JobOccurrence> stepOccurrences)
    {
        try
        {
            var mappings = JsonSerializer.Deserialize<Dictionary<string, string>>(dataMappingsJson);

            if (mappings == null || mappings.Count == 0)
                return jobData;

            var targetJson = !string.IsNullOrWhiteSpace(jobData) ? JsonNode.Parse(jobData)?.AsObject() : [];

            targetJson ??= [];

            foreach (var (source, target) in mappings)
            {
                // Source format: "stepId:jsonPath" or just "jsonPath" (from first parent)
                var sourceParts = source.Split(':', 2);

                string sourceOutput = null;

                if (sourceParts.Length == 2 && Guid.TryParse(sourceParts[0], out var sourceStepId))
                {
                    if (stepOccurrences.TryGetValue(sourceStepId, out var sourceOcc))
                        sourceOutput = sourceOcc.Result;
                }
                else
                {
                    // Use first completed parent's output
                    foreach (var occ in stepOccurrences.Values.Where(o => o.StepStatus == WorkflowStepStatus.Completed && !string.IsNullOrWhiteSpace(o.Result)))
                    {
                        sourceOutput = occ.Result;
                        break;
                    }
                }

                if (string.IsNullOrWhiteSpace(sourceOutput))
                    continue;

                try
                {
                    var sourceJson = JsonNode.Parse(sourceOutput);
                    var jsonPath = sourceParts.Length == 2 ? sourceParts[1] : sourceParts[0];

                    // Traverse dot-separated path (e.g., "ComplexProp.Title")
                    var value = GetJsonValue(sourceJson, jsonPath)?.DeepClone();

                    if (value != null)
                        SetJsonValue(targetJson, target, value);
                }
                catch
                {
                    // Skip invalid mappings
                }
            }

            return targetJson.ToJsonString();
        }
        catch
        {
            return jobData;
        }
    }

    private static JsonNode GetJsonValue(JsonNode json, string path)
    {
        var segments = path.TrimStart('$', '.').Split('.');
        var current = json;

        foreach (var segment in segments)
        {
            if (current == null)
                return null;

            if (current is JsonObject obj)
            {
                // Try exact match first, then case-insensitive fallback
                if (!obj.TryGetPropertyValue(segment, out var exactMatch))
                {
                    var ciKey = obj.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, segment, StringComparison.OrdinalIgnoreCase));
                    current = ciKey != null ? obj[ciKey] : null;
                }
                else
                {
                    current = exactMatch;
                }
            }
            else
            {
                current = current[segment];
            }
        }

        return current;
    }

    private static void SetJsonValue(JsonObject target, string path, JsonNode value)
    {
        var segments = path.TrimStart('$', '.').Split('.');
        var current = target;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (current[segments[i]] is not JsonObject nested)
            {
                nested = [];
                current[segments[i]] = nested;
            }

            current = nested;
        }

        current[segments[^1]] = value;
    }

    private void CancelRun(WorkflowRun run, DateTime now, string reason, List<WorkflowRun> runsToUpdate, List<JobOccurrence> occurrencesToUpdate, List<JobOccurrence> runningOccurrencesToCancel)
    {
        run.Status = WorkflowStatus.Cancelled;
        run.EndTime = now;
        run.Error = reason;

        if (run.StartTime.HasValue)
            run.DurationMs = (long)(now - run.StartTime.Value).TotalMilliseconds;

        var runningToCancel = run.StepOccurrences.Where(o => o.StepStatus == WorkflowStepStatus.Running).ToList();

        var pendingToSkip = run.StepOccurrences.Where(o => o.StepStatus is WorkflowStepStatus.Pending or WorkflowStepStatus.Delayed).ToList();

        runningOccurrencesToCancel.AddRange(runningToCancel);

        foreach (var occ in runningToCancel)
        {
            occ.StepStatus = WorkflowStepStatus.Cancelled;
            occ.Status = JobOccurrenceStatus.Cancelled;
            occ.EndTime = now;
        }

        foreach (var occ in pendingToSkip)
        {
            occ.StepStatus = WorkflowStepStatus.Skipped;
            occ.Status = JobOccurrenceStatus.Skipped;
            occ.EndTime = now;
            occ.Exception = $"Step skipped due to workflow cancellation: {reason}";
        }

        runsToUpdate.Add(run);
        occurrencesToUpdate.AddRange(runningToCancel);
        occurrencesToUpdate.AddRange(pendingToSkip);

        _logger.Warning("Workflow run {RunId} cancelled: {Reason}", run.Id, reason);
    }

    private void FailRun(WorkflowRun run, DateTime now, string reason, List<WorkflowRun> runsToUpdate, List<JobOccurrence> occurrencesToUpdate, List<JobOccurrence> runningOccurrencesToCancel)
    {
        run.Status = WorkflowStatus.Failed;
        run.EndTime = now;
        run.Error = reason;

        if (run.StartTime.HasValue)
            run.DurationMs = (long)(now - run.StartTime.Value).TotalMilliseconds;

        var runningToCancel = run.StepOccurrences.Where(o => o.StepStatus == WorkflowStepStatus.Running).ToList();

        var pendingToSkip = run.StepOccurrences.Where(o => o.StepStatus is WorkflowStepStatus.Pending or WorkflowStepStatus.Delayed).ToList();

        runningOccurrencesToCancel.AddRange(runningToCancel);

        foreach (var occ in runningToCancel)
        {
            occ.StepStatus = WorkflowStepStatus.Cancelled;
            occ.Status = JobOccurrenceStatus.Cancelled;
            occ.EndTime = now;
        }

        foreach (var occ in pendingToSkip)
        {
            occ.StepStatus = WorkflowStepStatus.Skipped;
            occ.Status = JobOccurrenceStatus.Skipped;
            occ.EndTime = now;
            occ.Exception = $"Step skipped due to workflow failure: {reason}";
        }

        runsToUpdate.Add(run);
        occurrencesToUpdate.AddRange(runningToCancel);
        occurrencesToUpdate.AddRange(pendingToSkip);

        _logger.Warning("Workflow run {RunId} failed: {Reason}", run.Id, reason);
    }
}
