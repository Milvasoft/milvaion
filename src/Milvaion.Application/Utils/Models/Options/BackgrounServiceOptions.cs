namespace Milvaion.Application.Utils.Models.Options;

/// <summary>
/// Configuration base options for background services.
/// </summary>
public class BackgrounServiceOptions
{
    /// <summary>
    /// Whether the service is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Memory tracking options for the background service.
    /// </summary>
    public MemoryTrackingOptions MemoryTrackingOptions { get; set; } = new();
}
