using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.Utils.Constants;
using System.ComponentModel;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.ActivityLogDtos;

/// <summary>
/// Data transfer object for activity log list.
/// </summary>
[Translate]
public class ActivityLogListDto : MilvaionBaseDto<int>
{
    /// <summary>
    /// The username who performed the activity.
    /// </summary>
    public string UserName { get; set; }

    /// <summary>
    /// Activity type.
    /// </summary>
    [DisplayFormat("{activityDescription}")]
    public UserActivity Activity { get; set; }

    /// <summary>
    /// User activity description.
    /// </summary>
    [Filterable(false)]
    [Browsable(false)]
    [LinkedWith<EnumFormatter<UserActivity>>(nameof(Activity), $"{EnumFormatter<UserActivity>.FormatterName}.{nameof(UserActivity)}")]
    public string ActivityDescription { get; set; }

    /// <summary>
    /// Date and time of the activity.
    /// </summary>
    [Filterable(FilterComponentType = UiInputConstant.DateTimeInput)]
    public DateTimeOffset ActivityDate { get; set; }

    /// <summary>
    /// Projection expression for mapping ActivityLog entity to ActivityLogListDto.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<ActivityLog, ActivityLogListDto>> Projection { get; } = a => new ActivityLogListDto
    {
        Id = a.Id,
        UserName = a.UserName,
        Activity = a.Activity,
        ActivityDate = a.ActivityDate,
    };
}
