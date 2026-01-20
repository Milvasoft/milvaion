using Milvaion.Application.Dtos.AccountDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Request;

namespace Milvaion.Application.Features.Account.InternalNotifications.GetAccountNotifications;

/// <summary>
/// Data transfer object for account details.
/// </summary>
public record GetAccountNotificationsQuery : ListRequest, IListRequestQuery<AccountNotificationDto>
{
    /// <summary>
    /// The user Id whose account details you want to access.
    /// </summary>
    public int UserId { get; set; }
}
