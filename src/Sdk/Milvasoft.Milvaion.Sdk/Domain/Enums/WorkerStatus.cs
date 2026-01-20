namespace Milvasoft.Milvaion.Sdk.Domain.Enums;

/// <summary>
/// Worker status enumeration.
/// </summary>
public enum WorkerStatus
{
    /// <summary>
    /// Worker is active and processing jobs.
    /// </summary>
    Active = 0,

    /// <summary>
    /// Worker is registered but not sending heartbeats.
    /// </summary>
    Inactive = 1,

    /// <summary>
    /// Worker hasn't sent heartbeat in threshold time (zombie detection).
    /// </summary>
    Zombie = 2,

    /// <summary>
    /// Worker gracefully shut down.
    /// </summary>
    Shutdown = 3
}
