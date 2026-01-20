using Milvaion.Application.Dtos.ContentManagementDtos.NamespaceDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Request;

namespace Milvaion.Application.Features.ContentManagement.Namespaces.GetNamespaceList;

/// <summary>
/// Data transfer object for contentNamespace list.
/// </summary>
public record GetNamespaceListQuery : ListRequest, IListRequestQuery<NamespaceListDto>
{
}