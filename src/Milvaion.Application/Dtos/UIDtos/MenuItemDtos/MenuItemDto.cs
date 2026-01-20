using Milvaion.Domain.UI;
using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.MultiLanguage.Manager;

namespace Milvaion.Application.Dtos.UIDtos.MenuItemDtos;

/// <summary>
/// Data transfer object for user list.
/// </summary>
[Translate]
[ExcludeFromMetadata]
public class MenuItemDto : MilvaionBaseDto<int>
{
    /// <summary>
    /// Name of menu item.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Frontend page url for navigate to on click.
    /// </summary>
    public string Url { get; set; }

    /// <summary>
    /// Order of menu item.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// Frontend page name for navigate to on click.
    /// </summary>
    public string PageName { get; set; }

    /// <summary>
    /// Related parent menu item id.
    /// </summary>
    public int? ParentId { get; set; }

    /// <summary>
    /// Menu item group information.
    /// </summary>
    public MenuGroupDto Group { get; set; }

    /// <summary>
    /// Children items of this menu items.
    /// </summary>
    public List<MenuItemDto> Childrens { get; set; }

    /// <summary>
    /// Projection expression for mapping MenuItem entity to MenuItem.
    /// </summary>
    public static Func<MenuItem, MenuItemDto> Projection(IMultiLanguageManager multiLanguageManager)
    {
        var menuItemNameLangExpression = multiLanguageManager.CreateTranslationMapExpression<MenuItem, MenuItemDto, MenuItemTranslation>(i => i.Name).Compile();
        var groupNameLangExpression = multiLanguageManager.CreateTranslationMapExpression<MenuGroup, NameIntNavigationDto, MenuGroupTranslation>(i => i.Name).Compile();

        return u => new MenuItemDto
        {
            Id = u.Id,
            Name = menuItemNameLangExpression(u),
            ParentId = u.ParentId,
            PageName = u.PageName,
            Url = u.Url,
            Order = u.Order,
            Group = new MenuGroupDto
            {
                Id = u.Group.Id,
                Name = groupNameLangExpression(u.Group),
                Order = u.Group.Order
            },
            Childrens = u.Childrens?.Select(Projection(multiLanguageManager)).ToList()
        };
    }
}