using Milvasoft.Components.CQRS.Command;
using Milvasoft.Types.Structs;

namespace Milvaion.Application.Features.Roles.UpdateRole;

/// <summary>
/// Data transfer object for role update.
/// </summary>
public class UpdateRoleCommand : MilvaionBaseDto<int>, ICommand<int>
{
    /// <summary>
    /// Name of role.
    /// </summary>
    public UpdateProperty<string> Name { get; set; }

    /// <summary>
    /// Related entities will always be updated according to the values ​​in this list. If you send it empty, related entities will be cleared. 
    /// If no update has been made, please send it with isUpdated false.
    /// </summary>
    public UpdateProperty<List<int>> PermissionIdList { get; set; }
}
