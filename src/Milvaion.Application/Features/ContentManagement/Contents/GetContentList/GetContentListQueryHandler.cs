using Milvaion.Application.Dtos.ContentManagementDtos.ContentDtos;
using Milvaion.Domain.ContentManagement;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.ContentManagement.Contents.GetContentList;

/// <summary>
/// Handles the content list operation.
/// </summary>
/// <param name="resourceGroupRepository"></param>
public class GetContentListQueryHandler(IMilvaionRepositoryBase<Content> resourceGroupRepository) : IInterceptable, IListQueryHandler<GetContentListQuery, ContentListDto>
{
    private readonly IMilvaionRepositoryBase<Content> _resourceGroupRepository = resourceGroupRepository;

    /// <inheritdoc/>
    public async Task<ListResponse<ContentListDto>> Handle(GetContentListQuery request, CancellationToken cancellationToken)
    {
        var response = await _resourceGroupRepository.GetAllAsync(request, projection: ContentListDto.Projection, cancellationToken: cancellationToken);

        return response;
    }
}
