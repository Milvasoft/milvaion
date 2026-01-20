using Milvasoft.Core.EntityBases.Concrete;
using System.ComponentModel.DataAnnotations.Schema;

namespace Milvaion.Domain;

/// <summary>
/// Entity of the RolePermissionRelations table.
/// </summary>
[Table(TableNames.RolePermissionRelations)]
public class RolePermissionRelation : BaseEntity<int>
{
    /// <summary>
    /// ID of the role.
    /// </summary>
    [ForeignKey(nameof(Role))]
    public int RoleId { get; set; }

    /// <summary>
    /// ID of the permission.
    /// </summary>
    [ForeignKey(nameof(Permission))]
    public int PermissionId { get; set; }

    /// <summary>
    /// Navigation property of Role relation.
    /// </summary>
    public virtual Role Role { get; set; }

    /// <summary>
    /// Navigation property of Permission relation.
    /// </summary>
    public virtual Permission Permission { get; set; }
}
