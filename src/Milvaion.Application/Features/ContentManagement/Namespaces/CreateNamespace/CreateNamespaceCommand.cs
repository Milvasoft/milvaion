using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Features.ContentManagement.Namespaces.CreateNamespace;

/// <summary>
/// Data transfer object for namespace creation.
/// </summary>
public record CreateNamespaceCommand : ICommand<int>
{
    /// <summary>
    /// Name of namespace.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Description of namespace.
    /// </summary>
    public string Description { get; set; }
}