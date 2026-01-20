namespace Milvasoft.Milvaion.Sdk.Worker.Options;

public class HeartbeatSettings
{
    /// <summary>
    /// Worker heartbeat interval in seconds (for Redis TTL refresh).
    /// Default: 30 seconds.
    /// </summary>
    public int IntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Job heartbeat interval in seconds (for zombie detection).
    /// Should be less than ZombieTimeoutMinutes to prevent false positives.
    /// Default: 60 seconds.
    /// </summary>
    public int JobHeartbeatIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Enable heartbeat service.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
