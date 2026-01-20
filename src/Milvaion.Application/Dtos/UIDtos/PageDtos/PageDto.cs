using Milvaion.Domain.UI;
using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.MultiLanguage.Manager;

namespace Milvaion.Application.Dtos.UIDtos.PageDtos;

/// <summary>
/// Page information.
/// </summary>
[ExcludeFromMetadata]
public class PageDto : MilvaionBaseDto<int>
{
    /// <summary>
    /// Frontend page name.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Frontend page localized name.
    /// </summary>
    [LinkedWith<PageNameTranslateFormatter>(nameof(Name), PageNameTranslateFormatter.FormatterName)]
    public string LocalizedName { get; set; }

    /// <summary>
    /// Determines whether the page has create action or not.
    /// </summary>
    public bool HasCreate { get; set; }

    /// <summary>
    /// Determines whether the page has detail action or not.
    /// </summary>
    public bool HasDetail { get; set; }

    /// <summary>
    /// Determines whether the page has edit action or not.
    /// </summary>
    public bool HasEdit { get; set; }

    /// <summary>
    /// Determines whether the page has delete action or not.
    /// </summary>
    public bool HasDelete { get; set; }

    /// <summary>
    /// Determines whether the user can create a new record in this page.
    /// </summary>
    public bool UserCanCreate { get; set; }

    /// <summary>
    /// Determines whether the user can access a record detail in this page.
    /// </summary>
    public bool UserCanDetail { get; set; }

    /// <summary>
    /// Determines whether the user can edit a record in this page.
    /// </summary>
    public bool UserCanEdit { get; set; }

    /// <summary>
    /// Determines whether the user can delete a record in this page.
    /// </summary>
    public bool UserCanDelete { get; set; }

    /// <summary>
    /// Navigation property of additional page action relation.    
    /// </summary>
    public List<PageActionDto> AdditionalActions { get; set; }

    /// <summary>
    /// Projection expression for mapping MenuItem entity to MenuItem.
    /// </summary>
    public static Func<Page, PageDto> Projection(IMultiLanguageManager multiLanguageManager) => p => new PageDto
    {
        Id = p.Id,
        Name = p.Name,
        HasCreate = p.HasCreate,
        HasDetail = p.HasDetail,
        HasEdit = p.HasEdit,
        HasDelete = p.HasDelete,
        AdditionalActions = [.. p.AdditionalActions.Select(PageActionDto.Projection(multiLanguageManager))]
    };
}
