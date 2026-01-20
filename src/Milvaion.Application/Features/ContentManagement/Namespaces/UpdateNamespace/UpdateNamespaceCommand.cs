using Milvasoft.Components.CQRS.Command;
using Milvasoft.Core.EntityBases.Concrete;
using Milvasoft.Types.Structs;

namespace Milvaion.Application.Features.ContentManagement.Namespaces.UpdateNamespace;

/// <summary>
/// Data transfer object for namespace update.
/// </summary>
public class UpdateNamespaceCommand : BaseDto<int>, ICommand<int>
{
    /// <summary>
    /// Name of namespace.
    /// </summary>
    public UpdateProperty<string> Name { get; set; }

    /// <summary>
    /// Description of namespace.
    /// </summary>
    public UpdateProperty<string> Description { get; set; }
}
