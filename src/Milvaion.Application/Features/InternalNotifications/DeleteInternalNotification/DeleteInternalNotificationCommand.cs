using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Features.InternalNotifications.DeleteInternalNotification;

/// <summary>
/// Data transfer object for internalNotification deletion.
/// </summary>
public record DeleteInternalNotificationCommand : ICommand<long>
{
    /// <summary>
    /// Id of the internalNotification to be deleted.
    /// </summary>
    public long InternalNotificationId { get; set; }
}
