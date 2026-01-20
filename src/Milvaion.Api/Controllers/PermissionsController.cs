using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Milvaion.Application.Dtos.PermissionDtos;
using Milvaion.Application.Features.Permissions.GetPermissionList;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.Attributes;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.PermissionManager;
using Milvasoft.Components.Rest.MilvaResponse;

namespace Milvaion.Api.Controllers;

/// <summary>
/// Permission endpoints.
/// </summary>
[ApiController]
[Route(GlobalConstant.FullRoute)]
[ApiVersion(GlobalConstant.CurrentApiVersion)]
[ApiExplorerSettings(GroupName = "v1.0")]
public class PermissionsController(IMediator mediator, IPermissionManager permissionManager) : ControllerBase
{
    private readonly IMediator _mediator = mediator;
    private readonly IPermissionManager _permissionManager = permissionManager;

    /// <summary>
    /// Get permissions in the system.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.PermissionManagement.List)]
    [HttpPatch]
    public Task<ListResponse<PermissionListDto>> GetPermissionsAsync(GetPermissionListQuery request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Migrates permissions to database.
    /// </summary>
    /// <returns></returns>
    [Auth(PermissionCatalog.App.SuperAdmin)]
    [HttpPut("migrate")]
    public Task<Response<string>> MigratePermissionsAsync() => _permissionManager.MigratePermissionsAsync(default);
}