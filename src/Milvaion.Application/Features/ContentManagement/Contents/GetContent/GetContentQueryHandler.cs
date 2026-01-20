using Milvaion.Application.Dtos.ContentManagementDtos.ContentDtos;
using Milvaion.Domain.ContentManagement;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Helpers;
using Milvasoft.Core.MultiLanguage.Manager;
using System.Linq.Expressions;

namespace Milvaion.Application.Features.ContentManagement.Contents.GetContent;

/// <summary>
/// Handles the content list operation.
/// </summary>
/// <param name="resourceGroupRepository"></param>
/// <param name="multiLanguageManager"></param>
public class GetContentQueryHandler(IMilvaionRepositoryBase<Content> resourceGroupRepository, IMultiLanguageManager multiLanguageManager) : IInterceptable, IQueryHandler<GetContentQuery, List<ContentDto>>
{
    private readonly IMilvaionRepositoryBase<Content> _resourceGroupRepository = resourceGroupRepository;
    private readonly IMultiLanguageManager _multiLanguageManager = multiLanguageManager;

    /// <inheritdoc/>
    public async Task<Response<List<ContentDto>>> Handle(GetContentQuery request, CancellationToken cancellationToken)
    {
        Expression<Func<Content, bool>> condition = c => true;

        switch (request.QueryType)
        {
            case ContentQueryType.Key:
                condition = c => c.KeyAlias == request.Query.Trim()
                              && c.NamespaceSlug == request.NamespaceSlug.Trim();
                break;
            case ContentQueryType.ResourceGroup:
                condition = c => c.ResourceGroupSlug == request.Query.Trim()
                              && c.NamespaceSlug == request.NamespaceSlug.Trim();
                break;
            case ContentQueryType.Namespace:
                condition = c => c.NamespaceSlug == request.NamespaceSlug.Trim();
                break;
            default:
                break;
        }

        var contents = await _resourceGroupRepository.GetAllAsync(condition: condition, projection: ContentDto.Projection, cancellationToken: cancellationToken);

        List<ContentDto> result = [];

        if (!contents.IsNullOrEmpty())
        {
            var currentLanguageId = _multiLanguageManager.GetCurrentLanguageId();

            var groupedByKey = contents.GroupBy(c => c.KeyAlias);

            foreach (var grouped in groupedByKey)
            {
                var currentLanguageContent = grouped.FirstOrDefault(c => c.LanguageId == currentLanguageId);

                if (currentLanguageContent != null)
                    result.Add(currentLanguageContent);
                else
                {
                    var defaultLanguageContent = grouped.FirstOrDefault(c => c.LanguageId == _multiLanguageManager.GetDefaultLanguageId());

                    if (defaultLanguageContent != null)
                        result.Add(defaultLanguageContent);
                    else
                        result.Add(grouped.First());
                }
            }
        }

        return Response<List<ContentDto>>.Success(result);
    }
}
