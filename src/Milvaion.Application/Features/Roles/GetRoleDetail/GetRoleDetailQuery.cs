using Milvaion.Application.Dtos.RoleDtos;
using Milvasoft.Components.CQRS.Query;

namespace Milvaion.Application.Features.Roles.GetRoleDetail;

/// <summary>
/// Data transfer object for role details.
/// </summary>
public record GetRoleDetailQuery : IQuery<RoleDetailDto>
{
    /// <summary>
    /// Role id to access details.
    /// </summary>
    public int RoleId { get; set; }
}
