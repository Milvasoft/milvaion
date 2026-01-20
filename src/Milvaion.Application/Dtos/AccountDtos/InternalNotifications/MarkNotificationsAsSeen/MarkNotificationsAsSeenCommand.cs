using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Dtos.AccountDtos.InternalNotifications.MarkNotificationsAsSeen;

/// <summary>
/// Data transfer object for mark as seen notification.
/// </summary>
public record MarkNotificationsAsSeenCommand : ICommand
{
    /// <summary>
    /// Mark the notifications as seen for the current user.
    /// </summary>
    public List<long> NotificationIdList { get; set; }

    /// <summary>
    /// Mark all notifications as seen if true.
    /// </summary>
    public bool MarkAll { get; set; }
}
