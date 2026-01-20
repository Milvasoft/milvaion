using Milvasoft.Core.EntityBases.Concrete;
using System.ComponentModel.DataAnnotations.Schema;

namespace Milvaion.Domain;

/// <summary>
/// Entity of the UserRoleRelations table.
/// </summary>
[Table(TableNames.UserRoleRelations)]
public class UserRoleRelation : BaseEntity<int>
{
    /// <summary>
    /// ID of the user.
    /// </summary>
    public int UserId { get; set; }

    /// <summary>
    /// ID of the role.
    /// </summary>
    public int RoleId { get; set; }

    /// <summary>
    /// Navigation property of Role relation.
    /// </summary>
    public virtual Role Role { get; set; }

    /// <summary>
    /// Navigation property of User relation.
    /// </summary>
    public virtual User User { get; set; }
}
