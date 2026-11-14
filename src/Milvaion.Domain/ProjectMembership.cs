using Microsoft.EntityFrameworkCore;
using Milvaion.Domain.Enums;
using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.EntityBases.Concrete.Auditing;
using System.ComponentModel.DataAnnotations.Schema;

namespace Milvaion.Domain;

/// <summary>
/// Gives a user a project role inside one project.
/// </summary>
[Table(TableNames.ProjectMemberships)]
[Index(nameof(ProjectId), nameof(UserId), IsUnique = true)]
[Index(nameof(UserId))]
[DontIndexCreationDate]
public class ProjectMembership : AuditableEntity<int>
{
    /// <summary>
    /// Id of the project.
    /// </summary>
    public int ProjectId { get; set; }

    /// <summary>
    /// Id of the user.
    /// </summary>
    public int UserId { get; set; }

    /// <summary>
    /// Id of the role. Must be a role with <see cref="RoleScope.Project"/>.
    /// </summary>
    public int RoleId { get; set; }

    /// <summary>
    /// Where the membership comes from. External memberships are replaced at every sign-in, local ones are kept.
    /// </summary>
    public ExternalProvider Source { get; set; } = ExternalProvider.Local;

    /// <summary>
    /// Navigation property of the project.
    /// </summary>
    public virtual Project Project { get; set; }

    /// <summary>
    /// Navigation property of the user.
    /// </summary>
    public virtual User User { get; set; }

    /// <summary>
    /// Navigation property of the role.
    /// </summary>
    public virtual Role Role { get; set; }
}
