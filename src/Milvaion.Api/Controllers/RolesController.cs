using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Milvaion.Application.Dtos.RoleDtos;
using Milvaion.Application.Features.Roles.CreateRole;
using Milvaion.Application.Features.Roles.DeleteRole;
using Milvaion.Application.Features.Roles.GetRoleDetail;
using Milvaion.Application.Features.Roles.GetRoleList;
using Milvaion.Application.Features.Roles.UpdateRole;
using Milvaion.Application.Utils.Attributes;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.PermissionManager;
using Milvaion.Domain.Enums;
using Milvasoft.Components.Rest.MilvaResponse;

namespace Milvaion.Api.Controllers;

/// <summary>
/// Role endpoints.
/// </summary>
[ApiController]
[Route(GlobalConstant.FullRoute)]
[ApiVersion(GlobalConstant.CurrentApiVersion)]
[ApiExplorerSettings(GroupName = "v1.0")]
[UserTypeAuth(UserType.Manager)]
public class RolesController(IMediator mediator) : ControllerBase
{
    private readonly IMediator _mediator = mediator;

    /// <summary>
    /// Gets roles.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.RoleManagement.List)]
    [HttpPatch]
    public Task<ListResponse<RoleListDto>> GetRolesAsync(GetRoleListQuery request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Get role according to role id.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.RoleManagement.Detail)]
    [HttpGet("role")]
    public Task<Response<RoleDetailDto>> GetRoleAsync([FromQuery] GetRoleDetailQuery request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Adds role.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.RoleManagement.Create)]
    [HttpPost("role")]
    public Task<Response<int>> AddRoleAsync(CreateRoleCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Updates role. Only the fields that are sent as isUpdated true are updated.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.RoleManagement.Update)]
    [HttpPut("role")]
    public Task<Response<int>> UpdateRoleAsync(UpdateRoleCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Removes role.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.RoleManagement.Delete)]
    [HttpDelete("role")]
    public Task<Response<int>> RemoveRoleAsync([FromQuery] DeleteRoleCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);
}