using Milvaion.Domain.JsonModels;
using Milvasoft.Core.EntityBases.Concrete.Auditing;
using Milvasoft.Core.MultiLanguage.EntityBases.Abstract;
using System.ComponentModel.DataAnnotations.Schema;

namespace Milvaion.Domain.UI;

/// <summary>
/// Entity of the PageActions table.
/// </summary>
[Table(TableNames.PageActions)]
public class PageAction : CreationAuditableEntity<int>, IHasTranslation<PageActionTranslation>
{
    /// <summary>
    /// Frontend action or page name.
    /// </summary>
    public string ActionName { get; set; }

    /// <summary>
    /// Related permission names. If the user has one of this permissions, the action will be visible.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public List<string> Permissions { get; set; }

    /// <summary>
    /// Translations of the page action.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public List<PageActionTranslation> Translations { get; set; }

    /// <summary>
    /// Related page id.
    /// </summary>
    [ForeignKey(nameof(Page))]
    public int PageId { get; set; }

    /// <summary>
    /// Navigation property of page relation.
    /// </summary>
    public virtual Page Page { get; set; }
}
