using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.EntityBases.Concrete.Auditing;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Milvaion.Domain;

/// <summary>
/// Entity of the Roles table.
/// </summary>
[Table(TableNames.Roles)]
[DontIndexCreationDate]
public class Role : FullAuditableEntity<int>
{
    /// <summary>
    /// Name of the role.
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; }

    /// <summary>
    /// Navigation property of users relation.
    /// </summary>
    [CascadeOnDelete]
    public virtual List<UserRoleRelation> UserRoleRelations { get; set; }

    /// <summary>
    /// Navigation property of permission relation.
    /// </summary>
    [CascadeOnDelete]
    public virtual List<RolePermissionRelation> RolePermissionRelations { get; set; }
}
