namespace Milvaion.Domain.Enums;

/// <summary>
/// Notification types.
/// </summary>
public enum NotificationType : byte
{
    /// <summary>
    /// Order approved notification.
    /// </summary>
    OrderApproved = 0,

    /// <summary>
    /// Job was automatically disabled due to consecutive failures.
    /// </summary>
    JobAutoDisabled = 1,

    /// <summary>
    /// Job was manually re-enabled after being auto-disabled.
    /// </summary>
    JobReEnabled = 2
}
