using Milvaion.Application.Dtos.ContentManagementDtos.ContentDtos;
using Milvaion.Domain.ContentManagement;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Enums;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.ContentManagement.Contents.GetContentDetail;

/// <summary>
/// Handles the content detail operation.
/// </summary>
/// <param name="resourceGroupRepository"></param>
public class GetContentDetailQueryHandler(IMilvaionRepositoryBase<Content> resourceGroupRepository) : IInterceptable, IQueryHandler<GetContentDetailQuery, ContentDetailDto>
{
    private readonly IMilvaionRepositoryBase<Content> _resourceGroupRepository = resourceGroupRepository;

    /// <inheritdoc/>
    public async Task<Response<ContentDetailDto>> Handle(GetContentDetailQuery request, CancellationToken cancellationToken)
    {
        var resourceGroup = await _resourceGroupRepository.GetByIdAsync(request.ContentId, projection: ContentDetailDto.Projection, cancellationToken: cancellationToken);

        if (resourceGroup == null)
            return Response<ContentDetailDto>.Success(resourceGroup, MessageKey.ContentNotFound, MessageType.Warning);

        return Response<ContentDetailDto>.Success(resourceGroup);
    }
}
