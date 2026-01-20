using Milvaion.Domain.JsonModels;
using Milvasoft.Core.EntityBases.Concrete.Auditing;
using Milvasoft.Core.MultiLanguage.EntityBases.Abstract;
using System.ComponentModel.DataAnnotations.Schema;

namespace Milvaion.Domain.UI;

/// <summary>
/// Entity of the MenuGroups table.
/// </summary>
[Table(TableNames.MenuGroups)]
public class MenuGroup : CreationAuditableEntity<int>, IHasTranslation<MenuGroupTranslation>
{
    /// <summary>
    /// Order of menu group.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// Translations of the menu group.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public List<MenuGroupTranslation> Translations { get; set; }

    /// <summary>
    /// Navigation property of menu items relation.
    /// </summary>
    public virtual List<MenuItem> MenuItems { get; set; }
}
