namespace Milvaion.Application.Utils.Enums;

/// <summary>
/// Overall system health enum
/// </summary>
public enum SystemHealth
{
    /// <summary>
    /// All systems operational
    /// </summary>
    Healthy,

    /// <summary>
    /// Some warnings detected
    /// </summary>
    Warning,

    /// <summary>
    /// Critical issues detected
    /// </summary>
    Critical,

    /// <summary>
    /// System degraded (dispatcher disabled)
    /// </summary>
    Degraded
}
