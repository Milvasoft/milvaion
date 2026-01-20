using Milvaion.Application.Dtos.NotificationDtos;
using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Features.InternalNotifications.CreateInternalNotification;

/// <summary>
/// Data transfer object for internalNotification creation.
/// </summary>
public class CreateInternalNotificationCommand : InternalNotificationRequest, ICommand
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CreateInternalNotificationCommand"/> class.
    /// </summary>
    public CreateInternalNotificationCommand()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CreateInternalNotificationCommand"/> class.
    /// </summary>
    /// <param name="type"></param>
    /// <param name="data"></param>
    public CreateInternalNotificationCommand(NotificationType type, object data) : base(type, data)
    {
    }
}