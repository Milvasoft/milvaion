using Microsoft.EntityFrameworkCore;
using Milvasoft.Core.EntityBases.Concrete.Auditing;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;

namespace Milvaion.Domain.UI;

/// <summary>
/// Entity of the Pages table.
/// </summary>
[Table(TableNames.Pages)]
[Index(nameof(Name), IsUnique = true)]
public class Page : CreationAuditableEntity<int>
{
    /// <summary>
    /// Frontend page name.
    /// </summary>
    public string Name { get; set; }

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
    /// Create action permission names. If the user has one of this permissions, the action will be visible.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public List<string> CreatePermissions { get; set; }

    /// <summary>
    /// Detail action permission names. If the user has one of this permissions, the action will be visible.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public List<string> DetailPermissions { get; set; }

    /// <summary>
    /// Edit action permission names. If the user has one of this permissions, the action will be visible.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public List<string> EditPermissions { get; set; }

    /// <summary>
    /// Delete action permission names. If the user has one of this permissions, the action will be visible.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public List<string> DeletePermissions { get; set; }

    /// <summary>
    /// Navigation property of additional page action relation.    
    /// </summary>
    public virtual List<PageAction> AdditionalActions { get; set; }

    #region Projections

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
    public static class Projections
    {
        public static Expression<Func<Page, Page>> PageInfo { get; } = p => new Page
        {
            Id = p.Id,
            Name = p.Name,
            HasCreate = p.HasCreate,
            HasDetail = p.HasDetail,
            HasEdit = p.HasEdit,
            HasDelete = p.HasDelete,
            CreatePermissions = p.CreatePermissions,
            DetailPermissions = p.DetailPermissions,
            EditPermissions = p.EditPermissions,
            DeletePermissions = p.DeletePermissions,
            AdditionalActions = p.AdditionalActions.Select(pa => new PageAction
            {
                Id = pa.Id,
                ActionName = pa.ActionName,
                PageId = pa.PageId,
                Permissions = pa.Permissions,
                Translations = pa.Translations
            }).ToList()
        };
    }
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member

    #endregion
}
