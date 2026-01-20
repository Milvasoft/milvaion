using Milvasoft.Components.CQRS.Command;
using Milvasoft.Types.Structs;

namespace Milvaion.Application.Features.InternalNotifications.UpdateInternalNotification;

/// <summary>
/// Data transfer object for internalNotification update.
/// </summary>
public class UpdateInternalNotificationCommand : MilvaionBaseDto<long>, ICommand<long>
{
    /// <summary>
    /// The type of notification (e.g., NewComment, NewFollower).
    /// </summary>
    public UpdateProperty<NotificationType> Type { get; set; }

    /// <summary>
    /// JSON payload for dynamic content and localization.
    /// </summary>
    public UpdateProperty<object> Data { get; set; }

    /// <summary>
    /// Text content of the notification. Can be null. 
    /// If it is null, Data property should be used in frontend(text generated with translations in frontend) for dynamic content.
    /// </summary>
    public UpdateProperty<string> Text { get; set; }

    /// <summary>
    /// Client-side URL to navigate on click.
    /// </summary>
    public UpdateProperty<string> ActionLink { get; set; }

    /// <summary>
    /// Related entity type (optional).
    /// </summary>
    public UpdateProperty<NotificationEntity> RelatedEntity { get; set; }

    /// <summary>
    /// Related entity ID (optional).
    /// </summary>
    public UpdateProperty<string> RelatedEntityId { get; set; }
}
