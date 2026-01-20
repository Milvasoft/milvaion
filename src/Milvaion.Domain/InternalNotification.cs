using Microsoft.EntityFrameworkCore;
using Milvaion.Domain.Enums;
using Milvasoft.Core.EntityBases.Concrete.Auditing;
using System.ComponentModel.DataAnnotations.Schema;

namespace Milvaion.Domain;

/// <summary>
/// Internal generic notification entity. Unseen notifications are deleted in 60 days, seen notifications in 30 days.
/// </summary>
[Table(TableNames.InternalNotifications)]
[Index(nameof(RecipientUserName))]
public class InternalNotification : CreationAuditableEntity<long>
{
    /// <summary>
    /// The user who will receive this notification.
    /// </summary>
    public string RecipientUserName { get; set; }

    /// <summary>
    /// Recipient user identifier.
    /// </summary>
    public int RecipientUserId { get; set; }

    /// <summary>
    /// Notification type.
    /// </summary>
    public NotificationType Type { get; set; }

    /// <summary>
    /// Date when the user marked this notification as seen.
    /// Null if unseen.
    /// </summary>
    public DateTime? SeenDate { get; set; }

    /// <summary>
    /// Text content of the notification. Can be null. 
    /// If it is null, Data property should be used in frontend(text generated with translations in frontend) for dynamic content.
    /// </summary>
    public string Text { get; set; }

    /// <summary>
    /// JSON data payload for dynamic content or localization.
    /// </summary>
    public string Data { get; set; }

    /// <summary>
    /// Notification related entity.
    /// </summary>
    public NotificationEntity RelatedEntityType { get; set; }

    /// <summary>
    /// Related entity identifier. Can be null.
    /// </summary>
    public string RelatedEntityId { get; set; }

    /// <summary>
    /// Client-side URL/route to navigate when the notification is clicked.
    /// </summary>
    public string ActionLink { get; set; }
}
