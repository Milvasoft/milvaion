using Milvaion.Application.Dtos.AccountDtos;
using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Features.Account.RefreshLogin;

/// <summary>
/// Data transfer object for refresh login operation.
/// </summary>
public record RefreshLoginCommand : ICommand<LoginResponseDto>
{
    /// <summary>
    /// Username to log in.
    /// </summary>
    public string UserName { get; set; }

    /// <summary>
    /// Unique Id of the device being logged in. Can be MacAddress or something.
    /// </summary>
    public string DeviceId { get; set; }

    /// <summary>
    /// Refresh token received during login.
    /// </summary>
    public string RefreshToken { get; set; }
}
