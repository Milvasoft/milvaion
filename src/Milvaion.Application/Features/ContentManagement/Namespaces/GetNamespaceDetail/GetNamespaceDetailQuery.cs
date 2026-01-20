using Milvaion.Application.Dtos.ContentManagementDtos.NamespaceDtos;
using Milvasoft.Components.CQRS.Query;

namespace Milvaion.Application.Features.ContentManagement.Namespaces.GetNamespaceDetail;

/// <summary>
/// Data transfer object for contentNamespace details.
/// </summary>
public record GetNamespaceDetailQuery : IQuery<NamespaceDetailDto>
{
    /// <summary>
    /// Namespace id to access details.
    /// </summary>
    public int NamespaceId { get; set; }
}
