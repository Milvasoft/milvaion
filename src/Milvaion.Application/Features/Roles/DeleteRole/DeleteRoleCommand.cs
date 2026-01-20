using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Features.Roles.DeleteRole;

/// <summary>
/// Data transfer object for role deletion.
/// </summary>
public record DeleteRoleCommand : ICommand<int>
{
    /// <summary>
    /// Id of the role to be deleted.
    /// </summary>
    public int RoleId { get; set; }
}
