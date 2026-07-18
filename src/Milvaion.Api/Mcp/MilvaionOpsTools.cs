using MediatR;
using Milvaion.Application.Features.Dashboard.GetDashboard;
using Milvaion.Application.Features.Workers.DeleteWorker;
using Milvaion.Application.Features.Workers.GetWorkerDetail;
using Milvaion.Application.Features.Workers.GetWorkerList;
using Milvaion.Application.Features.Workflows.CancelWorkflow;
using Milvaion.Application.Features.Workflows.CreateWorkflow;
using Milvaion.Application.Features.Workflows.DeleteWorkflow;
using Milvaion.Application.Features.Workflows.GetWorkflowDetail;
using Milvaion.Application.Features.Workflows.GetWorkflowList;
using Milvaion.Application.Features.Workflows.GetWorkflowRunDetail;
using Milvaion.Application.Features.Workflows.GetWorkflowRunList;
using Milvaion.Application.Features.Workflows.TriggerWorkflow;
using Milvaion.Application.Features.Workflows.UpdateWorkflow;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Types.Structs;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Milvaion.Api.Mcp;

/// <summary>
/// MCP tools covering workers, workflows and the overall health of the installation.
/// </summary>
[McpServerToolType]
public class MilvaionOpsTools(IMediator mediator, McpPermissionGuard guard)
{
    private readonly IMediator _mediator = mediator;
    private readonly McpPermissionGuard _guard = guard;

    private const int _maxPageSize = 100;

    /// <summary>
    /// Gets dashboard statistics for the installation.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>Job counts, execution status counts, throughput and worker health.</returns>
    [McpServerTool(Name = "get_overview")]
    [Description("Gets a high level snapshot of the Milvaion installation: job counts, execution status counts, throughput and worker health. Good first call when asked how things are doing.")]
    public async Task<object> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.ScheduledJobManagement.List);

        var response = await _mediator.Send(new GetDashboardQuery(), cancellationToken);

        return response.Data;
    }

    /// <summary>
    /// Gets workers with their heartbeat status and executable job types.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>Worker list.</returns>
    [McpServerTool(Name = "list_workers")]
    [Description("Lists worker processes with their heartbeat status and the job types each can execute. Use this when a job is not running to check whether a worker capable of executing it is actually alive.")]
    public async Task<object> ListWorkersAsync(CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.WorkerManagement.List);

        var response = await _mediator.Send(new GetWorkerListQuery(), cancellationToken);

        return new { workers = response.Data };
    }

    /// <summary>
    /// Gets workflows.
    /// </summary>
    /// <param name="searchTerm">Free text search over workflow names.</param>
    /// <param name="pageNumber">Page number, starting at 1.</param>
    /// <param name="pageSize">Results per page, capped at <see cref="_maxPageSize"/>.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Paged workflow list with the total count.</returns>
    [McpServerTool(Name = "list_workflows")]
    [Description("Lists workflows - multi step job pipelines with branching. Use this to find a workflow's id.")]
    public async Task<object> ListWorkflowsAsync(
        [Description("Optional free text search over workflow names.")] string searchTerm = null,
        [Description("Page number, starting at 1.")] int pageNumber = 1,
        [Description("Results per page. Maximum 100.")] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.WorkflowManagement.List);

        var response = await _mediator.Send(new GetWorkflowListQuery
        {
            SearchTerm = searchTerm,
            PageNumber = pageNumber < 1 ? 1 : pageNumber,
            RowCount = Math.Clamp(pageSize, 1, _maxPageSize)
        }, cancellationToken);

        return new
        {
            totalCount = response.TotalDataCount,
            pageNumber,
            workflows = response.Data
        };
    }

    /// <summary>
    /// Gets workflow runs, optionally for a single workflow.
    /// </summary>
    /// <param name="workflowId">Workflow id to filter runs for. Null lists runs across all workflows.</param>
    /// <param name="pageNumber">Page number, starting at 1.</param>
    /// <param name="pageSize">Results per page, capped at <see cref="_maxPageSize"/>.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Paged workflow run list with the total count.</returns>
    [McpServerTool(Name = "list_workflow_runs")]
    [Description("Lists workflow runs with their status, so you can see which pipelines completed, failed or are still going.")]
    public async Task<object> ListWorkflowRunsAsync(
        [Description("Optional workflow GUID id to list runs for. Omit to list runs across all workflows.")] Guid? workflowId = null,
        [Description("Page number, starting at 1.")] int pageNumber = 1,
        [Description("Results per page. Maximum 100.")] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.WorkflowManagement.List);

        var response = await _mediator.Send(new GetWorkflowRunListQuery
        {
            WorkflowId = workflowId,
            PageNumber = pageNumber < 1 ? 1 : pageNumber,
            RowCount = Math.Clamp(pageSize, 1, _maxPageSize)
        }, cancellationToken);

        return new
        {
            totalCount = response.TotalDataCount,
            pageNumber,
            runs = response.Data
        };
    }

    /// <summary>
    /// Triggers a workflow run immediately.
    /// </summary>
    /// <param name="workflowId">Workflow id to trigger.</param>
    /// <param name="reason">Trigger reason, recorded against the run alongside the calling credential.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>The created workflow run.</returns>
    /// <exception cref="McpException">Thrown when the workflow could not be triggered.</exception>
    [McpServerTool(Name = "trigger_workflow")]
    [Description("Starts a workflow run immediately. This has real side effects in production - confirm with the user before calling it. Requires the workflow trigger permission.")]
    public async Task<object> TriggerWorkflowAsync(
        [Description("The workflow's GUID id.")] Guid workflowId,
        [Description("Why the workflow is being triggered. Recorded against the run.")] string reason = null,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.WorkflowManagement.Trigger);

        var response = await _mediator.Send(new TriggerWorkflowCommand
        {
            WorkflowId = workflowId,
            // Attributing the trigger matters: someone reading the history later needs to know a machine did it.
            Reason = string.IsNullOrWhiteSpace(reason)
                ? $"Triggered via MCP by {_guard.CallerName}"
                : $"{reason} (via MCP by {_guard.CallerName})"
        }, cancellationToken);

        if (!response.IsSuccess)
            throw new McpException(response.Messages?.FirstOrDefault()?.Message ?? "Failed to trigger the workflow.");

        return new
        {
            run = response.Data,
            message = "Workflow triggered. Use list_workflow_runs to follow its progress."
        };
    }

    /// <summary>
    /// Gets a worker in full.
    /// </summary>
    /// <param name="workerId">Worker id to access details.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Worker detail.</returns>
    /// <exception cref="McpException">Thrown when no worker exists with the given id.</exception>
    [McpServerTool(Name = "get_worker")]
    [Description("Gets one worker in full: heartbeat, capacity and the job types it can execute. Use this to confirm a worker can actually run a given job type before creating or repointing a job at it.")]
    public async Task<object> GetWorkerAsync(
        [Description("The worker id, as returned by list_workers.")] string workerId,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.WorkerManagement.Detail);

        var response = await _mediator.Send(new GetWorkerDetailQuery { WorkerId = workerId }, cancellationToken);

        if (response.Data == null)
            throw new McpException($"No worker found with id '{workerId}'.");

        return response.Data;
    }

    /// <summary>
    /// Deletes a worker registration.
    /// </summary>
    /// <remarks>
    /// Removes the registration only. A worker process that is still alive re-registers on its next heartbeat.
    /// </remarks>
    /// <param name="workerId">Worker id to delete.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Id of the deleted worker.</returns>
    /// <exception cref="McpException">Thrown when the worker could not be deleted.</exception>
    [McpServerTool(Name = "delete_worker")]
    [Description("Removes a worker registration from Milvaion. This is for tidying up records of workers that no longer exist - it does not stop a running process, and a live worker simply re-registers on its next heartbeat. Jobs pointed at the deleted worker will stop being executed until a worker with that id appears again.")]
    public async Task<object> DeleteWorkerAsync(
        [Description("The worker id.")] string workerId,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.WorkerManagement.Delete);

        var response = await _mediator.Send(new DeleteWorkerCommand { WorkerId = workerId }, cancellationToken);

        if (!response.IsSuccess)
            throw new McpException(response.Messages?.FirstOrDefault()?.Message ?? "Failed to delete the worker.");

        return new { workerId = response.Data, message = "Worker registration removed." };
    }

    /// <summary>
    /// Gets a workflow in full, including its step graph.
    /// </summary>
    /// <param name="workflowId">Workflow id to access details.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Workflow detail.</returns>
    /// <exception cref="McpException">Thrown when no workflow exists with the given id.</exception>
    [McpServerTool(Name = "get_workflow")]
    [Description("Gets one workflow in full: its steps, edges, conditions, data mappings and failure strategy. Use this to explain what a pipeline does or to work out which step is responsible for a failure.")]
    public async Task<object> GetWorkflowAsync(
        [Description("The workflow's GUID id, as returned by list_workflows.")] Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.WorkflowManagement.Detail);

        var response = await _mediator.Send(new GetWorkflowDetailQuery { WorkflowId = workflowId }, cancellationToken);

        if (response.Data == null)
            throw new McpException($"No workflow found with id {workflowId}.");

        return response.Data;
    }

    /// <summary>
    /// Gets a workflow run in full, including per step outcomes.
    /// </summary>
    /// <param name="runId">Workflow run id to access details.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Workflow run detail.</returns>
    /// <exception cref="McpException">Thrown when no workflow run exists with the given id.</exception>
    [McpServerTool(Name = "get_workflow_run")]
    [Description("Gets one workflow run with the outcome of each step, so you can see exactly where a pipeline stopped and which branch it took.")]
    public async Task<object> GetWorkflowRunAsync(
        [Description("The run's GUID id, as returned by list_workflow_runs.")] Guid runId,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.WorkflowManagement.Detail);

        var response = await _mediator.Send(new GetWorkflowRunDetailQuery { RunId = runId }, cancellationToken);

        if (response.Data == null)
            throw new McpException($"No workflow run found with id {runId}.");

        return response.Data;
    }

    /// <summary>
    /// Cancels a running workflow run.
    /// </summary>
    /// <param name="workflowRunId">Workflow run id to cancel.</param>
    /// <param name="reason">Cancellation reason, recorded against the run.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Whether the run was cancelled.</returns>
    /// <exception cref="McpException">Thrown when the run could not be cancelled.</exception>
    [McpServerTool(Name = "cancel_workflow_run")]
    [Description("Cancels a workflow run that is still in progress. Steps already running are signalled to stop; steps not yet started are skipped.")]
    public async Task<object> CancelWorkflowRunAsync(
        [Description("The run's GUID id.")] Guid workflowRunId,
        [Description("Why it is being cancelled.")] string reason = null,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.WorkflowManagement.Trigger);

        var response = await _mediator.Send(new CancelWorkflowCommand
        {
            WorkflowRunId = workflowRunId,
            Reason = string.IsNullOrWhiteSpace(reason)
                ? $"Cancelled via MCP by {_guard.CallerName}"
                : $"{reason} (via MCP by {_guard.CallerName})"
        }, cancellationToken);

        if (!response.IsSuccess)
            throw new McpException(response.Messages?.FirstOrDefault()?.Message ?? "Failed to cancel the workflow run.");

        return new { cancelled = response.Data, message = "Workflow run cancelled." };
    }

    /// <summary>
    /// Deletes a workflow along with its run history.
    /// </summary>
    /// <param name="workflowId">Workflow id to delete.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Id of the deleted workflow.</returns>
    /// <exception cref="McpException">Thrown when the workflow could not be deleted.</exception>
    [McpServerTool(Name = "delete_workflow")]
    [Description("Permanently deletes a workflow and its run history. This cannot be undone. If the intent is only to stop it running, call set_workflow_active with false instead. Confirm explicitly with the user before calling.")]
    public async Task<object> DeleteWorkflowAsync(
        [Description("The workflow's GUID id.")] Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.WorkflowManagement.Delete);

        var response = await _mediator.Send(new DeleteWorkflowCommand { WorkflowId = workflowId }, cancellationToken);

        if (!response.IsSuccess)
            throw new McpException(response.Messages?.FirstOrDefault()?.Message ?? "Failed to delete the workflow.");

        return new { workflowId = response.Data, message = "Workflow deleted." };
    }

    /// <summary>
    /// Creates a workflow with its step graph.
    /// </summary>
    /// <param name="name">Display name of the workflow.</param>
    /// <param name="steps">Steps making up the graph. Each needs a tempId that edges refer to.</param>
    /// <param name="edges">Directed connections between steps, referencing step tempIds.</param>
    /// <param name="description">Description of the workflow.</param>
    /// <param name="tags">Comma separated tags.</param>
    /// <param name="cronExpression">Six part cron expression for recurring execution, or null for manual only.</param>
    /// <param name="failureStrategy">What to do when a step fails.</param>
    /// <param name="maxStepRetries">Maximum retries per step.</param>
    /// <param name="timeoutSeconds">Timeout for the whole workflow.</param>
    /// <param name="isActive">Whether the workflow is scheduled straight away.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Id of the created workflow.</returns>
    /// <exception cref="McpException">Thrown when the workflow could not be created.</exception>
    [McpServerTool(Name = "create_workflow")]
    [Description("Creates a workflow: a graph of job steps with directed edges between them. Each step needs a tempId; edges connect steps by referencing those tempIds. Task steps need a jobId from list_jobs. The graph must be acyclic. Condition nodes route through 'true' and 'false' source ports. Build the graph carefully and show the user the intended flow before calling - a wrong edge produces a workflow that runs but does the wrong thing.")]
    public async Task<object> CreateWorkflowAsync(
        [Description("Display name of the workflow.")] string name,
        [Description("Steps in the graph. Each needs a unique tempId; Task nodes need a jobId.")] List<CreateWorkflowStepDto> steps,
        [Description("Edges connecting steps by tempId. Use sourcePort 'true' or 'false' when the source is a condition node.")] List<CreateWorkflowEdgeDto> edges,
        [Description("Description of what the workflow does.")] string description = null,
        [Description("Comma separated tags.")] string tags = null,
        [Description("Six part cron expression (second minute hour day month dayOfWeek). Omit for manual only.")] string cronExpression = null,
        [Description("What to do when a step fails.")] WorkflowFailureStrategy failureStrategy = WorkflowFailureStrategy.StopOnFirstFailure,
        [Description("Maximum retries per step.")] int maxStepRetries = 0,
        [Description("Timeout for the whole workflow in seconds.")] int? timeoutSeconds = null,
        [Description("Whether the workflow is scheduled straight away. Pass false to create it paused.")] bool isActive = true,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.WorkflowManagement.Create);

        var response = await _mediator.Send(new CreateWorkflowCommand
        {
            Name = name,
            Description = description,
            Tags = tags,
            IsActive = isActive,
            FailureStrategy = failureStrategy,
            MaxStepRetries = maxStepRetries,
            TimeoutSeconds = timeoutSeconds,
            CronExpression = cronExpression,
            Steps = steps ?? [],
            Edges = edges ?? []
        }, cancellationToken);

        if (!response.IsSuccess)
            throw new McpException(response.Messages?.FirstOrDefault()?.Message ?? "Failed to create the workflow.");

        return new
        {
            workflowId = response.Data,
            message = "Workflow created. Use get_workflow with this id to verify the graph."
        };
    }

    /// <summary>
    /// Pauses or resumes a workflow.
    /// </summary>
    /// <remarks>
    /// A metadata-only update, so unlike a definition change it is allowed while runs are in progress.
    /// </remarks>
    /// <param name="workflowId">Workflow id.</param>
    /// <param name="isActive">True to resume scheduling, false to pause it.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Id of the updated workflow.</returns>
    /// <exception cref="McpException">Thrown when the workflow could not be updated.</exception>
    [McpServerTool(Name = "set_workflow_active")]
    [Description("Pauses or resumes a workflow without touching its definition. Works even while runs are in progress, so this is the right tool for stopping a misbehaving workflow from starting again. Running instances are unaffected - use cancel_workflow_run for those.")]
    public async Task<object> SetWorkflowActiveAsync(
        [Description("The workflow's GUID id.")] Guid workflowId,
        [Description("True to resume scheduling, false to pause.")] bool isActive,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.WorkflowManagement.Update);

        var response = await _mediator.Send(new UpdateWorkflowCommand
        {
            WorkflowId = workflowId,
            IsActive = new UpdateProperty<bool> { Value = isActive, IsUpdated = true }
        }, cancellationToken);

        if (!response.IsSuccess)
            throw new McpException(response.Messages?.FirstOrDefault()?.Message ?? "Failed to update the workflow.");

        return new
        {
            workflowId = response.Data,
            isActive,
            message = isActive ? "Workflow resumed." : "Workflow paused. Runs already in progress are unaffected."
        };
    }

    /// <summary>
    /// Updates a workflow. Only the arguments that are supplied are changed.
    /// </summary>
    /// <remarks>
    /// Steps and edges are replaced as a unit: supply both or neither. Supplying a definition is rejected while
    /// runs are in progress, whereas metadata-only changes are always allowed.
    /// </remarks>
    /// <param name="workflowId">Workflow id to update.</param>
    /// <param name="name">New name, or null to leave unchanged.</param>
    /// <param name="description">New description, or null to leave unchanged.</param>
    /// <param name="tags">New comma separated tags, or null to leave unchanged.</param>
    /// <param name="cronExpression">New cron expression, or null to leave unchanged.</param>
    /// <param name="failureStrategy">New failure strategy, or null to leave unchanged.</param>
    /// <param name="maxStepRetries">New retry count, or null to leave unchanged.</param>
    /// <param name="timeoutSeconds">New timeout, or null to leave unchanged.</param>
    /// <param name="steps">Replacement steps. Must be supplied together with <paramref name="edges"/>.</param>
    /// <param name="edges">Replacement edges. Must be supplied together with <paramref name="steps"/>.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Id of the updated workflow.</returns>
    /// <exception cref="McpException">Thrown when the workflow could not be updated.</exception>
    [McpServerTool(Name = "update_workflow")]
    [Description("Updates a workflow. Only the arguments you supply are changed. To rewire the graph, supply both steps and edges - they replace the existing definition as a unit and cannot be changed while runs are in progress. Call get_workflow first so you are editing from the current definition rather than guessing it. To pause a workflow use set_workflow_active instead.")]
    public async Task<object> UpdateWorkflowAsync(
        [Description("The workflow's GUID id.")] Guid workflowId,
        [Description("New display name. Omit to leave unchanged.")] string name = null,
        [Description("New description. Omit to leave unchanged.")] string description = null,
        [Description("New comma separated tags. Omit to leave unchanged.")] string tags = null,
        [Description("New six part cron expression. Omit to leave unchanged.")] string cronExpression = null,
        [Description("New failure strategy. Omit to leave unchanged.")] WorkflowFailureStrategy? failureStrategy = null,
        [Description("New maximum retries per step. Omit to leave unchanged.")] int? maxStepRetries = null,
        [Description("New workflow timeout in seconds. Omit to leave unchanged.")] int? timeoutSeconds = null,
        [Description("Replacement steps. Supply together with edges to rewire the graph.")] List<CreateWorkflowStepDto> steps = null,
        [Description("Replacement edges. Supply together with steps to rewire the graph.")] List<CreateWorkflowEdgeDto> edges = null,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.WorkflowManagement.Update);

        if (steps is null != edges is null)
            throw new McpException("Steps and edges must be supplied together. Send both to replace the graph, or neither to change only settings.");

        var command = new UpdateWorkflowCommand { WorkflowId = workflowId };

        if (name is not null)
            command.Name = new UpdateProperty<string> { Value = name, IsUpdated = true };

        if (description is not null)
            command.Description = new UpdateProperty<string> { Value = description, IsUpdated = true };

        if (tags is not null)
            command.Tags = new UpdateProperty<string> { Value = tags, IsUpdated = true };

        if (cronExpression is not null)
            command.CronExpression = new UpdateProperty<string> { Value = cronExpression, IsUpdated = true };

        if (failureStrategy is not null)
            command.FailureStrategy = new UpdateProperty<WorkflowFailureStrategy> { Value = failureStrategy.Value, IsUpdated = true };

        if (maxStepRetries is not null)
            command.MaxStepRetries = new UpdateProperty<int> { Value = maxStepRetries.Value, IsUpdated = true };

        if (timeoutSeconds is not null)
            command.TimeoutSeconds = new UpdateProperty<int?> { Value = timeoutSeconds, IsUpdated = true };

        if (steps is not null)
        {
            command.Steps = new UpdateProperty<List<CreateWorkflowStepDto>> { Value = steps, IsUpdated = true };
            command.Edges = new UpdateProperty<List<CreateWorkflowEdgeDto>> { Value = edges, IsUpdated = true };
        }

        var response = await _mediator.Send(command, cancellationToken);

        if (!response.IsSuccess)
            throw new McpException(response.Messages?.FirstOrDefault()?.Message ?? "Failed to update the workflow.");

        return new
        {
            workflowId = response.Data,
            message = "Workflow updated. Use get_workflow to confirm the result."
        };
    }
}
