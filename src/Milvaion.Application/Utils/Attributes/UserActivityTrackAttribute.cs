using Milvasoft.Interception.Decorator;

namespace Milvaion.Application.Utils.Attributes;

/// <summary>
/// Specifies that the method marked with this attribute will be added to as activity to database by the <see cref="UserActivityLogInterceptor"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class UserActivityTrackAttribute(UserActivity activity) : DecorateAttribute(typeof(UserActivityLogInterceptor))
{
    /// <summary>
    /// Activity type.
    /// </summary>
    public UserActivity Activity { get; set; } = activity;
}
