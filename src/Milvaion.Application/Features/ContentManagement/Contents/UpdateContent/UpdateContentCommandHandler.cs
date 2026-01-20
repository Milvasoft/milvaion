using Mapster;
using Milvaion.Application.Dtos.ContentManagementDtos.ContentDtos;
using Milvaion.Domain.ContentManagement;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Ef.Transaction;
using Milvasoft.Interception.Interceptors.Logging;
using Milvasoft.Types.Structs;

namespace Milvaion.Application.Features.ContentManagement.Contents.UpdateContent;

/// <summary>
/// Handles the update of the content.
/// </summary>
/// <param name="ContentRepository"></param>
/// <param name="MediaRepository"></param>
[Log]
[Transaction]
[UserActivityTrack(UserActivity.UpdateContent)]
public record UpdateContentCommandHandler(IMilvaionRepositoryBase<Content> ContentRepository, IMilvaionRepositoryBase<Media> MediaRepository) : IInterceptable, ICommandHandler<UpdateContentCommand, int>
{
    private readonly IMilvaionRepositoryBase<Content> _resourceGroupRepository = ContentRepository;
    private readonly IMilvaionRepositoryBase<Media> _mediaRepository = MediaRepository;

    /// <inheritdoc/>
    public async Task<Response<int>> Handle(UpdateContentCommand request, CancellationToken cancellationToken)
    {
        if (request.Medias.IsUpdated)
        {
            await _mediaRepository.ExecuteDeleteAsync(rl => rl.ContentId == request.Id, cancellationToken: cancellationToken);

            var newMedias = request.Medias.Value?.Adapt<List<Media>>();

            if (newMedias?.Count > 0)
                await _mediaRepository.BulkAddAsync(newMedias, null, cancellationToken);
        }

        request.Medias = new UpdateProperty<List<UpsertMediaDto>>();

        var setPropertyBuilder = _resourceGroupRepository.GetUpdatablePropertiesBuilder(request);

        await _resourceGroupRepository.ExecuteUpdateAsync(request.Id, setPropertyBuilder, cancellationToken: cancellationToken);

        return Response<int>.Success(request.Id);
    }
}
