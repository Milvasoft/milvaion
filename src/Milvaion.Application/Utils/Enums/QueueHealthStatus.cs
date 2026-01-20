namespace Milvaion.Application.Utils.Enums;

/// <summary>
/// Queue health status enum
/// </summary>
public enum QueueHealthStatus
{
    /// <summary>
    /// Queue is healthy
    /// </summary>
    Healthy,

    /// <summary>
    /// Queue depth is concerning
    /// </summary>
    Warning,

    /// <summary>
    /// Queue is at critical capacity
    /// </summary>
    Critical,

    /// <summary>
    /// Queue is unavailable
    /// </summary>
    Unavailable
}
