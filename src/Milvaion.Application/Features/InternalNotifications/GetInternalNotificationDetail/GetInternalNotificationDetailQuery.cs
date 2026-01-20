using Milvaion.Application.Dtos.NotificationDtos;
using Milvasoft.Components.CQRS.Query;

namespace Milvaion.Application.Features.InternalNotifications.GetInternalNotificationDetail;

/// <summary>
/// Data transfer object for internalNotification details.
/// </summary>
public record GetInternalNotificationDetailQuery : IQuery<InternalNotificationDetailDto>
{
    /// <summary>
    /// InternalNotification id to access details.
    /// </summary>
    public long InternalNotificationId { get; set; }
}
