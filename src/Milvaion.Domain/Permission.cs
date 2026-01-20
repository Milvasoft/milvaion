using Microsoft.EntityFrameworkCore;
using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.EntityBases.Concrete;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Milvaion.Domain;

/// <summary>
/// Entity of the Permissions table.
/// </summary>
[Table(TableNames.Permissions)]
[Index(nameof(PermissionGroup), nameof(Name), IsUnique = true)]
[DontIndexCreationDate]
public class Permission : BaseEntity<int>
{
    /// <summary>
    /// Name of the permission.
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; }

    /// <summary>
    /// Name of the permission.
    /// </summary>
    [MaxLength(255)]
    public string Description { get; set; }

    /// <summary>
    /// Normalized name of the permission for consistent comparison.
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string NormalizedName { get; set; }

    /// <summary>
    /// Group name or category the permission belongs to.
    /// </summary>
    [MaxLength(100)]
    public string PermissionGroup { get; set; }

    /// <summary>
    /// Group name or category the permission belongs to.
    /// </summary>
    [MaxLength(255)]
    public string PermissionGroupDescription { get; set; }

    /// <summary>
    /// Navigation property of role relation.
    /// </summary>
    public virtual List<RolePermissionRelation> RolePermissionRelations { get; set; }

    /// <summary>
    /// Formats the permission and group name with a dot.
    /// </summary>
    /// <returns></returns>
    public string FormatPermissionAndGroup() => $"{PermissionGroup}.{Name}";
}
