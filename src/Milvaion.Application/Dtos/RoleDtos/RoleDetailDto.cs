using Milvasoft.Attributes.Annotations;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.RoleDtos;

/// <summary>
/// Data transfer object for role details.
/// </summary>
[Translate]
[ExcludeFromMetadata]
public class RoleDetailDto : MilvaionBaseDto<int>
{
    /// <summary>
    /// Name of role. (e.g. Viewer, Editor)
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Users belonging to the role.
    /// </summary>
    public List<NameIntNavigationDto> Users { get; set; }

    /// <summary>
    /// Permissions belonging to the role.
    /// </summary>
    public List<NameIntNavigationDto> Permissions { get; set; }

    /// <summary>
    /// Information about record audit.
    /// </summary>
    public AuditDto<int> AuditInfo { get; set; }

    /// <summary>
    /// Projection expression for mapping Role entity to RoleDetailDto.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<Role, RoleDetailDto>> Projection { get; } = r => new RoleDetailDto
    {
        Id = r.Id,
        Name = r.Name,
        Permissions = r.RolePermissionRelations.Select(p => new NameIntNavigationDto
        {
            Id = p.PermissionId,
            Name = p.Permission.Name,
        }).ToList(),
        Users = r.UserRoleRelations.Select(p => new NameIntNavigationDto
        {
            Id = p.UserId,
            Name = p.User.UserName,
        }).ToList(),
        AuditInfo = new AuditDto<int>(r)
    };
}
