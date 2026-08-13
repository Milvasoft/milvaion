using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Milvaion.Application.Dtos.WorkflowDtos;
using Milvaion.Application.Features.Workflows.CancelWorkflow;
using Milvaion.Application.Features.Workflows.CreateWorkflow;
using Milvaion.Application.Features.Workflows.DeleteWorkflow;
using Milvaion.Application.Features.Workflows.GetWorkflowDetail;
using Milvaion.Application.Features.Workflows.GetWorkflowList;
using Milvaion.Application.Features.Workflows.GetWorkflowRunDetail;
using Milvaion.Application.Features.Workflows.GetWorkflowRunList;
using Milvaion.Application.Features.Workflows.TriggerWorkflow;
using Milvaion.Application.Features.Workflows.UpdateWorkflow;
using Milvaion.Application.Utils.Attributes;
using Milvasoft.Components.Rest.MilvaResponse;

namespace Milvaion.Api.Controllers;

/// <summary>
/// Workflow (Job Chaining / DAG) endpoints.
/// </summary>
[ApiController]
[Route(GlobalConstant.FullRoute)]
[ApiVersion(GlobalConstant.CurrentApiVersion)]
[ApiExplorerSettings(GroupName = "v1.0")]
[Auth]
public class WorkflowsController(IMediator mediator) : ControllerBase
{
    private readonly IMediator _mediator = mediator;

    /// <summary>
    /// Gets workflows.
    /// </summary>
    [Auth(PermissionCatalog.WorkflowManagement.List)]
    [HttpPatch]
    public Task<ListResponse<WorkflowListDto>> GetWorkflowsAsync(GetWorkflowListQuery request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Gets workflow detail with steps.
    /// </summary>
    [Auth(PermissionCatalog.WorkflowManagement.Detail)]
    [HttpGet("workflow")]
    public Task<Response<WorkflowDetailDto>> GetWorkflowAsync([FromQuery] GetWorkflowDetailQuery request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Creates a new workflow with steps (DAG definition).
    /// </summary>
    [Auth(PermissionCatalog.WorkflowManagement.Create)]
    [HttpPost("workflow")]
    public Task<Response<Guid>> CreateWorkflowAsync(CreateWorkflowCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Updates an existing workflow's settings.
    /// </summary>
    [Auth(PermissionCatalog.WorkflowManagement.Update)]
    [HttpPut("workflow")]
    public Task<Response<Guid>> UpdateWorkflowAsync(UpdateWorkflowCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Deletes a workflow.
    /// </summary>
    [Auth(PermissionCatalog.WorkflowManagement.Delete)]
    [HttpDelete("workflow")]
    public Task<Response<Guid>> DeleteWorkflowAsync([FromQuery] DeleteWorkflowCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Triggers a workflow run (creates a WorkflowRun and dispatches root steps).
    /// </summary>
    [Auth(PermissionCatalog.WorkflowManagement.Trigger)]
    [HttpPost("workflow/trigger")]
    public Task<Response<TriggerWorkflowResponse>> TriggerWorkflowAsync(TriggerWorkflowCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Cancels a running workflow (cancels running steps, skips pending steps).
    /// </summary>
    [Auth(PermissionCatalog.WorkflowManagement.Update)]
    [HttpPost("workflow/cancel")]
    public Task<Response<bool>> CancelWorkflowAsync(CancelWorkflowCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Gets workflow runs with optional workflow filter.
    /// </summary>
    [Auth(PermissionCatalog.WorkflowManagement.List)]
    [HttpPatch("runs")]
    public Task<ListResponse<WorkflowRunListDto>> GetWorkflowRunsAsync(GetWorkflowRunListQuery request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Gets workflow run detail with step run states (for DAG visualization).
    /// </summary>
    [Auth(PermissionCatalog.WorkflowManagement.Detail)]
    [HttpGet("runs/run")]
    public Task<Response<WorkflowRunDetailDto>> GetWorkflowRunDetailAsync([FromQuery] GetWorkflowRunDetailQuery request, CancellationToken cancellation) => _mediator.Send(request, cancellation);
}
