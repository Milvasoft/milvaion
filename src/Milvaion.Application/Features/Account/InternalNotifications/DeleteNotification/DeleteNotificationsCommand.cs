using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Features.Account.InternalNotifications.DeleteNotification;

/// <summary>
/// Data transfer object for delete notification.
/// </summary>
public record DeleteNotificationsCommand : ICommand
{
    /// <summary>
    /// Delete the notifications for the current user.
    /// </summary>
    public List<long> NotificationIdList { get; set; }

    /// <summary>
    /// Delete all notifications.
    /// </summary>
    public bool DeleteAll { get; set; }
}
