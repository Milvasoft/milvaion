using Milvaion.Application.Dtos.UserDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Request;

namespace Milvaion.Application.Features.Users.GetUserList;

/// <summary>
/// Data transfer object for user list.
/// </summary>
public record GetUserListQuery : ListRequest, IListRequestQuery<UserListDto>
{
}