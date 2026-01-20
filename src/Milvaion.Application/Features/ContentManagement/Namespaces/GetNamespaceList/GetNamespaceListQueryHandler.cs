using Milvaion.Application.Dtos.ContentManagementDtos.NamespaceDtos;
using Milvaion.Domain.ContentManagement;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.ContentManagement.Namespaces.GetNamespaceList;

/// <summary>
/// Handles the contentNamespace list operation.
/// </summary>
/// <param name="contentNamespaceRepository"></param>
public class GetNamespaceListQueryHandler(IMilvaionRepositoryBase<Namespace> contentNamespaceRepository) : IInterceptable, IListQueryHandler<GetNamespaceListQuery, NamespaceListDto>
{
    private readonly IMilvaionRepositoryBase<Namespace> _contentNamespaceRepository = contentNamespaceRepository;

    /// <inheritdoc/>
    public async Task<ListResponse<NamespaceListDto>> Handle(GetNamespaceListQuery request, CancellationToken cancellationToken)
    {
        var response = await _contentNamespaceRepository.GetAllAsync(request, projection: NamespaceListDto.Projection, cancellationToken: cancellationToken);

        return response;
    }
}
