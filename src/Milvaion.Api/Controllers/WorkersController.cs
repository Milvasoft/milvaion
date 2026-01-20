using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Milvaion.Application.Dtos.WorkerDtos;
using Milvaion.Application.Features.Workers.DeleteWorker;
using Milvaion.Application.Features.Workers.GetWorkerDetail;
using Milvaion.Application.Features.Workers.GetWorkerList;
using Milvaion.Application.Utils.Attributes;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.PermissionManager;
using Milvaion.Domain.Enums;
using Milvasoft.Components.Rest.MilvaResponse;

namespace Milvaion.Api.Controllers;

/// <summary>
/// Workers endpoints for managing registered workers.
/// </summary>
[ApiController]
[Route(GlobalConstant.FullRoute)]
[ApiVersion(GlobalConstant.CurrentApiVersion)]
[ApiExplorerSettings(GroupName = "v1.0")]
[UserTypeAuth(UserType.Manager)]
public class WorkersController(IMediator mediator) : ControllerBase
{
    private readonly IMediator _mediator = mediator;

    /// <summary>
    /// Gets workers.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.WorkerManagement.List)]
    [HttpPatch]
    public Task<Response<List<WorkerDto>>> GetWorkersAsync(GetWorkerListQuery request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Get worker according to worker id.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.WorkerManagement.Detail)]
    [HttpGet("worker")]
    public Task<Response<WorkerDto>> GetWorkerAsync([FromQuery] GetWorkerDetailQuery request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Removes worker.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.WorkerManagement.Delete)]
    [HttpDelete("worker")]
    public Task<Response<string>> RemoveWorkerAsync([FromQuery] DeleteWorkerCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);
}
