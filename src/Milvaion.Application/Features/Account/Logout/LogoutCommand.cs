using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Features.Account.Logout;

/// <summary>
/// Data transfer object for logout operation.
/// </summary>
public record LogoutCommand : ICommand
{
    /// <summary>
    /// Username to logged out.
    /// </summary>
    public string UserName { get; set; }

    /// <summary>
    /// Unique Id of the device being logged out. Can be MacAddress or something.
    /// </summary>
    public string DeviceId { get; set; }
}
