using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Features.Users.CreateUser;

/// <summary>
/// Data transfer object for user creation.
/// </summary>
public record CreateUserCommand : ICommand<int>
{
    /// <summary>
    /// Type of user.
    /// </summary>
    public UserType UserType { get; set; }

    /// <summary>
    /// Unique username of the user. (e.g. johndoe)
    /// </summary>
    public string UserName { get; set; }

    /// <summary>
    /// Email of user.
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
    /// Password of the user.    
    /// </summary>
    public string Password { get; set; }

    /// <summary>
    /// The id list of the role the user will be assigned to.
    /// </summary>
    public List<int> RoleIdList { get; set; }

    /// <summary>
    /// Allowed notification types for the user.
    /// </summary>
    public List<NotificationType> AllowedNotifications { get; set; }
}