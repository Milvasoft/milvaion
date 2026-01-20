using Milvaion.Application.Dtos.ContentManagementDtos.ResourceGroupDtos;
using Milvaion.Domain.ContentManagement;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.ContentManagement.ResourceGroups.GetResourceGroupList;

/// <summary>
/// Handles the resource group list operation.
/// </summary>
/// <param name="resourceGroupRepository"></param>
public class GetResourceGroupListQueryHandler(IMilvaionRepositoryBase<ResourceGroup> resourceGroupRepository) : IInterceptable, IListQueryHandler<GetResourceGroupListQuery, ResourceGroupListDto>
{
    private readonly IMilvaionRepositoryBase<ResourceGroup> _resourceGroupRepository = resourceGroupRepository;

    /// <inheritdoc/>
    public async Task<ListResponse<ResourceGroupListDto>> Handle(GetResourceGroupListQuery request, CancellationToken cancellationToken)
    {
        var response = await _resourceGroupRepository.GetAllAsync(request, projection: ResourceGroupListDto.Projection, cancellationToken: cancellationToken);

        return response;
    }
}
