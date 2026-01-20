using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Features.Account.ChangePassword;

/// <summary>
/// Data transfer object for password change operation.
/// </summary>
public record ChangePasswordCommand : ICommand
{
    /// <summary>
    /// Username whose password will be changed
    /// </summary>
    public string UserName { get; set; }

    /// <summary>
    /// Old password of account.
    /// </summary>
    public string OldPassword { get; set; }

    /// <summary>
    /// New password.
    /// </summary>
    public string NewPassword { get; set; }
}
