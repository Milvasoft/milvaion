using Mapster;
using Milvaion.Domain.ContentManagement;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.ContentManagement.Contents.CreateBulkContent;

/// <summary>
/// Handles the creation of the content.
/// </summary>
/// <param name="ContentRepository"></param>
/// <param name="ResourceGroupRepository"></param>
[Log]
[UserActivityTrack(UserActivity.CreateContent)]
public record CreateBulkContentCommandHandler(IMilvaionRepositoryBase<Content> ContentRepository, IMilvaionRepositoryBase<ResourceGroup> ResourceGroupRepository) : IInterceptable, ICommandHandler<CreateBulkContentCommand>
{
    private readonly IMilvaionRepositoryBase<Content> _contentRepository = ContentRepository;
    private readonly IMilvaionRepositoryBase<ResourceGroup> _resourceGroupRepository = ResourceGroupRepository;

    /// <inheritdoc/>
    public async Task<Response> Handle(CreateBulkContentCommand request, CancellationToken cancellationToken)
    {
        var relatedResourceGroups = await _resourceGroupRepository.GetAllAsync(rg => request.Contents.Select(r => r.ResourceGroupId).Distinct().Contains(rg.Id),
                                                                              projection: ResourceGroup.Projections.CreateContent,
                                                                              cancellationToken: cancellationToken);

        List<Content> contents = [];

        foreach (var requestContent in request.Contents)
        {
            var relatedResourceGroup = relatedResourceGroups.Find(rg => rg.Id == requestContent.ResourceGroupId);

            if (relatedResourceGroup is null)
                continue;

            var content = request.Adapt<Content>();

            content.Key = requestContent.Key.Trim();
            content.LanguageId = requestContent.LanguageId;
            content.Value = requestContent.Value;
            content.ResourceGroupId = relatedResourceGroup.Id;
            content.NamespaceId = relatedResourceGroup.NamespaceId;
            content.ResourceGroupSlug = relatedResourceGroup.Slug;
            content.NamespaceSlug = relatedResourceGroup.Namespace.Slug;
            content.KeyAlias = content.BuildKeyAlias();

            content.Medias = requestContent.Medias?.Select(m => new Media
            {
                Value = m.Media,
                Type = m.Type
            }).ToList();

            contents.Add(content);
        }

        await _contentRepository.BulkAddAsync(contents, cancellationToken: cancellationToken);

        return Response.Success();
    }
}
