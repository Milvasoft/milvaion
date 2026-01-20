using Milvasoft.Attributes.Annotations;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.UserDtos;

/// <summary>
/// Data transfer object for user details.
/// </summary>
[Translate]
[ExcludeFromMetadata]
public class UserDetailDto : MilvaionBaseDto<int>
{
    /// <summary>
    /// Unique username of the user.
    /// </summary>
    public string UserName { get; set; }

    /// <summary>
    /// Email of the user.
    /// </summary>
    public string Email { get; set; }

    /// <summary>
    /// Name of the user.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Surname of the user.
    /// </summary>
    public string Surname { get; set; }

    /// <summary>
    /// Information about the roles the user is assigned to.
    /// </summary>
    public List<NameIntNavigationDto> Roles { get; set; }

    /// <summary>
    /// Allowed notification types for the user.
    /// </summary>
    public List<NotificationType> AllowedNotifications { get; set; }

    /// <summary>
    /// Information about record audit.
    /// </summary>
    public AuditDto<int> AuditInfo { get; set; }

    /// <summary>
    /// Projection expression for mapping User entity to UserDetailDto.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<User, UserDetailDto>> Projection { get; } = u => new UserDetailDto
    {
        Id = u.Id,
        UserName = u.UserName,
        Email = u.Email,
        Name = u.Name,
        Surname = u.Surname,
        Roles = u.RoleRelations.Select(rr => new NameIntNavigationDto { Id = rr.Role.Id, Name = rr.Role.Name }).ToList(),
        AllowedNotifications = u.AllowedNotifications,
        AuditInfo = new AuditDto<int>(u)
    };
}
