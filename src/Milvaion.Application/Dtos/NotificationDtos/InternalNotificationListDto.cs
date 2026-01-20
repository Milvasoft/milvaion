using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Milvasoft.Attributes.Annotations;
using System.ComponentModel;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.NotificationDtos;

/// <summary>
/// Data transfer object for internalNotification list.
/// </summary>
[Translate]
public class InternalNotificationListDto : MilvaionBaseDto<long>
{
    /// <summary>
    /// The user who will receive this notification.
    /// </summary>
    public string RecipientUserName { get; set; }

    /// <summary>
    /// Notification type.
    /// </summary>
    public NotificationType Type { get; set; }

    /// <summary>
    /// Notification type description.
    /// </summary>
    [Filterable(false)]
    [Browsable(false)]
    [LinkedWith<EnumFormatter<NotificationType>>(nameof(EntityType), $"{EnumFormatter<NotificationType>.FormatterName}.{nameof(NotificationType)}")]
    public string TypeDescription { get; set; }

    /// <summary>
    /// Date when the user marked this notification as seen. Null if unseen.
    /// </summary>
    [ClientDefaultValue(MessageConstant.Hypen)]
    public DateTime? SeenDate { get; set; }

    /// <summary>
    /// Determines whether the notification is seen or not.
    /// </summary>
    [DisplayFormat("{isSeenDescription}")]
    public bool IsSeen => SeenDate.HasValue;

    /// <summary>
    /// Determines whether the notification is seen or not.
    /// </summary>
    [Filterable(false)]
    [Browsable(false)]
    [LinkedWith<YesNoFormatter>(nameof(IsSeen), YesNoFormatter.FormatterName)]
    public string IsSeenDescription { get; set; }

    /// <summary>
    /// Projection expression for mapping InternalNotification internalNotification to InternalNotificationListDto.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<InternalNotification, InternalNotificationListDto>> Projection { get; } = r => new InternalNotificationListDto
    {
        Id = r.Id,
        RecipientUserName = r.RecipientUserName,
        Type = r.Type,
        SeenDate = r.SeenDate
    };
}
