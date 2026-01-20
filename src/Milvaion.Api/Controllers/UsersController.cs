using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Milvaion.Application.Dtos.UserDtos;
using Milvaion.Application.Features.Users.CreateUser;
using Milvaion.Application.Features.Users.DeleteUser;
using Milvaion.Application.Features.Users.GetUserDetail;
using Milvaion.Application.Features.Users.GetUserList;
using Milvaion.Application.Features.Users.UpdateUser;
using Milvaion.Application.Utils.Attributes;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.PermissionManager;
using Milvaion.Domain.Enums;
using Milvasoft.Components.Rest.MilvaResponse;

namespace Milvaion.Api.Controllers;

/// <summary>
/// User endpoints.
/// </summary>
[ApiController]
[Route(GlobalConstant.FullRoute)]
[ApiVersion(GlobalConstant.CurrentApiVersion)]
[ApiExplorerSettings(GroupName = "v1.0")]
[UserTypeAuth(UserType.Manager)]
public class UsersController(IMediator mediator) : ControllerBase
{
    private readonly IMediator _mediator = mediator;

    /// <summary>
    /// Gets users.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.UserManagement.List)]
    [HttpPatch]
    public Task<ListResponse<UserListDto>> GetUsersAsync(GetUserListQuery request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Get user according to user id.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.UserManagement.Detail)]
    [HttpGet("user")]
    public Task<Response<UserDetailDto>> GetUserAsync([FromQuery] GetUserDetailQuery request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Adds user.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.UserManagement.Create)]
    [HttpPost("user")]
    public Task<Response<int>> AddUserAsync(CreateUserCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Updates user. Only the fields that are sent as isUpdated true are updated.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.UserManagement.Update)]
    [HttpPut("user")]
    public Task<Response<int>> UpdateUserAsync(UpdateUserCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Removes user.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.UserManagement.Delete)]
    [HttpDelete("user")]
    public Task<Response<int>> RemoveUserAsync([FromQuery] DeleteUserCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);
}