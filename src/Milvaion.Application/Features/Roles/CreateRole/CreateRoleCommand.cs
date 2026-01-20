using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Features.Roles.CreateRole;

/// <summary>
/// Data transfer object for role creation.
/// </summary>
public record CreateRoleCommand : ICommand<int>
{
    /// <summary>
    /// Name of role. (e.g. Viewer, Support, Developer)
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Role's permission id list.
    /// </summary>
    public List<int> PermissionIdList { get; set; }
}