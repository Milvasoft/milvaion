using Milvasoft.Components.CQRS.Command;
using Milvasoft.Core.EntityBases.Concrete;
using Milvasoft.Types.Structs;

namespace Milvaion.Application.Features.ContentManagement.ResourceGroups.UpdateResourceGroup;

/// <summary>
/// Data transfer object for resource group update.
/// </summary>
public class UpdateResourceGroupCommand : BaseDto<int>, ICommand<int>
{
    /// <summary>
    /// Name of resource group.
    /// </summary>
    public UpdateProperty<string> Name { get; set; }

    /// <summary>
    /// Description of resource group.
    /// </summary>
    public UpdateProperty<string> Description { get; set; }
}
