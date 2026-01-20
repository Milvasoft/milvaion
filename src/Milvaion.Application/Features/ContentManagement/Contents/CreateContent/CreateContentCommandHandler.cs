using Mapster;
using Milvaion.Domain.ContentManagement;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.ContentManagement.Contents.CreateContent;

/// <summary>
/// Handles the creation of the content.
/// </summary>
/// <param name="ContentRepository"></param>
/// <param name="ResourceGroupRepository"></param>
[Log]
[UserActivityTrack(UserActivity.CreateContent)]
public record CreateContentCommandHandler(IMilvaionRepositoryBase<Content> ContentRepository, IMilvaionRepositoryBase<ResourceGroup> ResourceGroupRepository) : IInterceptable, ICommandHandler<CreateContentCommand, int>
{
    private readonly IMilvaionRepositoryBase<Content> _contentRepository = ContentRepository;
    private readonly IMilvaionRepositoryBase<ResourceGroup> _resourceGroupRepository = ResourceGroupRepository;

    /// <inheritdoc/>
    public async Task<Response<int>> Handle(CreateContentCommand request, CancellationToken cancellationToken)
    {
        var content = request.Adapt<Content>();

        var relatedResourceGroup = await _resourceGroupRepository.GetByIdAsync(request.ResourceGroupId, projection: ResourceGroup.Projections.CreateContent, cancellationToken: cancellationToken);

        if (relatedResourceGroup is null)
            return Response<int>.Error(default, MessageKey.ResourceGroupNotFound);

        content.Key = request.Key.Trim();
        content.LanguageId = request.LanguageId;
        content.Value = request.Value;
        content.ResourceGroupId = relatedResourceGroup.Id;
        content.NamespaceId = relatedResourceGroup.NamespaceId;
        content.ResourceGroupSlug = relatedResourceGroup.Slug;
        content.NamespaceSlug = relatedResourceGroup.Namespace.Slug;
        content.KeyAlias = content.BuildKeyAlias();

        content.Medias = request.Medias?.Select(m => new Media
        {
            Value = m.Media,
            Type = m.Type
        }).ToList();

        await _contentRepository.AddAsync(content, cancellationToken);

        return Response<int>.Success(content.Id);
    }
}
