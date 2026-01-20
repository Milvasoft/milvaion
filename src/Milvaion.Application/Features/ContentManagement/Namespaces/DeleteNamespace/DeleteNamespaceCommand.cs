using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Features.ContentManagement.Namespaces.DeleteNamespace;

/// <summary>
/// Data transfer object for contentNamespace deletion.
/// </summary>
public record DeleteNamespaceCommand : ICommand<int>
{
    /// <summary>
    /// Id of the contentNamespace to be deleted.
    /// </summary>
    public int NamespaceId { get; set; }
}
