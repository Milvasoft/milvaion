using Milvasoft.Attributes.Annotations;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.UserDtos;

/// <summary>
/// Data transfer object for user list.
/// </summary>
[Translate]
public class UserListDto : MilvaionBaseDto<int>
{
    /// <summary>
    /// Unique username of user.
    /// </summary>
    public string UserName { get; set; }

    /// <summary>
    /// Email of user.
    /// </summary>
    [Filterable(false)]
    public string Email { get; set; }

    /// <summary>
    /// Name of user.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Surname of user.
    /// </summary>
    [Filterable(false)]
    public string Surname { get; set; }

    /// <summary>
    /// Projection expression for mapping User entity to UserListDto.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<User, UserListDto>> Projection { get; } = u => new UserListDto
    {
        Id = u.Id,
        UserName = u.UserName,
        Email = u.Email,
        Name = u.Name,
        Surname = u.Surname
    };
}