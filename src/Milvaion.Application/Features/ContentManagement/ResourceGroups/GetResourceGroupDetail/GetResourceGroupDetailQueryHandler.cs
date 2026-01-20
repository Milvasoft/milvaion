using Milvaion.Application.Dtos.ContentManagementDtos.ResourceGroupDtos;
using Milvaion.Domain.ContentManagement;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Enums;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.ContentManagement.ResourceGroups.GetResourceGroupDetail;

/// <summary>
/// Handles the resource group detail operation.
/// </summary>
/// <param name="resourceGroupRepository"></param>
public class GetResourceGroupDetailQueryHandler(IMilvaionRepositoryBase<ResourceGroup> resourceGroupRepository) : IInterceptable, IQueryHandler<GetResourceGroupDetailQuery, ResourceGroupDetailDto>
{
    private readonly IMilvaionRepositoryBase<ResourceGroup> _resourceGroupRepository = resourceGroupRepository;

    /// <inheritdoc/>
    public async Task<Response<ResourceGroupDetailDto>> Handle(GetResourceGroupDetailQuery request, CancellationToken cancellationToken)
    {
        var resourceGroup = await _resourceGroupRepository.GetByIdAsync(request.ResourceGroupId, projection: ResourceGroupDetailDto.Projection, cancellationToken: cancellationToken);

        if (resourceGroup == null)
            return Response<ResourceGroupDetailDto>.Success(resourceGroup, MessageKey.ResourceGroupNotFound, MessageType.Warning);

        return Response<ResourceGroupDetailDto>.Success(resourceGroup);
    }
}
