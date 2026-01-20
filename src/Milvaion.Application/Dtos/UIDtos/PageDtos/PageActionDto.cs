using Milvaion.Domain.UI;
using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.MultiLanguage.Manager;

namespace Milvaion.Application.Dtos.UIDtos.PageDtos;

/// <summary>
/// Page action information.
/// </summary>
[ExcludeFromMetadata]
public class PageActionDto : MilvaionBaseDto<int>
{
    /// <summary>
    /// Action localized title.
    /// </summary>
    public string Title { get; set; }

    /// <summary>
    /// Frontend action or page name.
    /// </summary>
    public string ActionName { get; set; }

    /// <summary>
    /// Url of action.
    /// </summary>
    public string Url { get; set; }

    /// <summary>
    /// If an action will be taken on the page, it takes the value true. If a redirection will be made to another page on the page, it takes the value false.
    /// </summary>
    public bool IsAction { get; set; }

    /// <summary>
    /// Projection expression for mapping PageAction entity to PageActionDto.
    /// </summary>
    public static Func<PageAction, PageActionDto> Projection(IMultiLanguageManager multiLanguageManager)
    {
        var pageActionLangExpression = multiLanguageManager.CreateTranslationMapExpression<PageAction, PageActionDto, PageActionTranslation>(i => i.Title).Compile();

        return pa => new PageActionDto
        {
            Id = pa.Id,
            Title = pageActionLangExpression(pa),
            ActionName = pa.ActionName
        };
    }
}
