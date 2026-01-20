using Milvaion.Domain.JsonModels;
using Milvasoft.Core.EntityBases.Concrete.Auditing;
using Milvasoft.Core.MultiLanguage.EntityBases.Abstract;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;

namespace Milvaion.Domain.UI;

/// <summary>
/// Entity of the MenuItem table.
/// </summary>
[Table(TableNames.MenuItems)]
public class MenuItem : CreationAuditableEntity<int>, IHasTranslation<MenuItemTranslation>
{
    /// <summary>
    /// Frontend page url for navigate to on click.
    /// </summary>
    public string Url { get; set; }

    /// <summary>
    /// Frontend page name for navigate to on click.
    /// </summary>
    public string PageName { get; set; }

    /// <summary>
    /// Order of the menu item.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// Related permission group or permission names. If the user has a permission from one of these group or permissions, the menu item will be visible.
    /// If this is a parent menu item, it will be the permission group name. e.g. UserManagement
    /// If this is a child menu, it will be the permission name. e.g. UserManagement.List
    /// </summary>
    [Column(TypeName = "jsonb")]
    public List<string> PermissionOrGroupNames { get; set; }

    /// <summary>
    /// Translations of the menu items.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public List<MenuItemTranslation> Translations { get; set; }

    /// <summary>
    /// Related parent group id.
    /// </summary>
    [ForeignKey(nameof(Group))]
    public int GroupId { get; set; }

    /// <summary>
    /// Navigation property of menu group relation.
    /// </summary>
    public virtual MenuGroup Group { get; set; }

    /// <summary>
    /// Related parent menu item id.
    /// </summary>
    [ForeignKey(nameof(Parent))]
    public int? ParentId { get; set; }

    /// <summary>
    /// Navigation property of menu item relation.
    /// </summary>
    public virtual MenuItem Parent { get; set; }

    /// <summary>
    /// Navigation property of children menu items relation.
    /// </summary>
    public virtual List<MenuItem> Childrens { get; set; }

    #region Projections

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
    public static class Projections
    {
        public static Expression<Func<MenuItem, MenuItem>> AccessibleMenuItems { get; } = u => new MenuItem
        {
            Id = u.Id,
            Url = u.Url,
            PageName = u.PageName,
            Order = u.Order,
            ParentId = u.ParentId,
            PermissionOrGroupNames = u.PermissionOrGroupNames,
            GroupId = u.GroupId,
            Group = u.Group,
            Translations = u.Translations,
            Parent = u.Parent
        };
    }

    #endregion
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
}
