using Milvaion.Application.Dtos.RoleDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Request;

namespace Milvaion.Application.Features.Roles.GetRoleList;

/// <summary>
/// Data transfer object for role list.
/// </summary>
public record GetRoleListQuery : ListRequest, IListRequestQuery<RoleListDto>
{
}