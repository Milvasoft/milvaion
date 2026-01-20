namespace Milvaion.Application.Dtos.NotificationDtos;

/// <summary>
/// Represents the data required to publish a new notification.
/// </summary>
/// <remarks>
/// Convenience constructor for required fields.
/// </remarks>
public class InternalNotificationRequest
{
    /// <summary>
    /// The type of notification (e.g., NewComment, NewFollower).
    /// </summary>
    public NotificationType Type { get; set; }

    /// <summary>
    /// JSON payload for dynamic content and localization.
    /// </summary>
    public object Data { get; set; }

    /// <summary>
    /// Text content of the notification. Can be null. 
    /// If it is null, Data property should be used in frontend(text generated with translations in frontend) for dynamic content.
    /// </summary>
    public string Text { get; set; }

    /// <summary>
    /// Client-side URL to navigate on click.
    /// </summary>
    public string ActionLink { get; set; }

    /// <summary>
    /// Related entity type (optional).
    /// </summary>
    public NotificationEntity RelatedEntity { get; set; } = NotificationEntity.None;

    /// <summary>
    /// Related entity ID (optional).
    /// </summary>
    public string RelatedEntityId { get; set; }

    /// <summary>
    /// Recipients' usernames. If empty, all users are considered.
    /// </summary>
    public List<string> Recipients { get; set; }

    /// <summary>
    /// Determines whether to find recipients based on their allowed notification types.
    /// </summary>
    public bool FindRecipientsFromType { get; set; } = true;

    /// <summary>
    /// Creates a new instance of InternalNotificationRequest.  
    /// </summary>
    public InternalNotificationRequest()
    {
    }

    /// <summary>
    /// Creates a new instance of InternalNotificationRequest.    
    /// </summary>
    /// <param name="type"></param>
    /// <param name="data"></param>
    public InternalNotificationRequest(NotificationType type, object data)
    {
        Type = type;
        Data = data;
    }
}