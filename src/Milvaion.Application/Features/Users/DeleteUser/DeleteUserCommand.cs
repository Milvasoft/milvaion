using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Features.Users.DeleteUser;

/// <summary>
/// Data transfer object for user deletion.
/// </summary>
public record DeleteUserCommand : ICommand<int>
{
    /// <summary>
    /// Id of the user to be deleted.
    /// </summary>
    public int UserId { get; set; }
}
