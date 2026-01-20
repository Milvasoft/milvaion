using Milvaion.Application.Dtos.AccountDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Request;

namespace Milvaion.Application.Features.InternalNotifications.GetInternalNotificationList;

/// <summary>
/// Data transfer object for internalNotification list.
/// </summary>
public record GetInternalNotificationListQuery : ListRequest, IListRequestQuery<AccountNotificationDto>
{
}