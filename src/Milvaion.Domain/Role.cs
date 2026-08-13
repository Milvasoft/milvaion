using Microsoft.EntityFrameworkCore;
using Milvaion.Domain.Enums;
using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.EntityBases.Concrete.Auditing;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Milvaion.Domain;

/// <summary>
/// Entity of the Roles table.
/// </summary>
[Table(TableNames.Roles)]
[Index(nameof(Name), nameof(IsDeleted), nameof(DeletionDate), IsUnique = true)]
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
    /// Where this role is owned. <see cref="ExternalProvider.Local"/> roles are managed entirely in
    /// Milvaion. Roles provisioned from an identity provider are owned there: their name mirrors the
    /// external group and cannot be renamed here, but their permission set is assigned in Milvaion.
    /// </summary>
    public ExternalProvider Provider { get; set; } = ExternalProvider.Local;

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
