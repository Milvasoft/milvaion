using Milvaion.Application.Dtos.PermissionDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Request;

namespace Milvaion.Application.Features.Permissions.GetPermissionList;

/// <summary>
/// Data transfer object for permission details.
/// </summary>
public record GetPermissionListQuery : ListRequest, IListRequestQuery<PermissionListDto>
{
}
