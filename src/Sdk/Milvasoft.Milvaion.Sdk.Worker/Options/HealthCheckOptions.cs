namespace Milvasoft.Milvaion.Sdk.Worker.Options;

/// <summary>
/// Configuration options for worker health checks.
/// </summary>
public class HealthCheckOptions
{
    /// <summary>
    /// Health check enabled flag.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Health check file path.
    /// </summary>
    public string ReadyFilePath { get; set; } = "/tmp/live";

    /// <summary>
    /// Health check file path.
    /// </summary>
    public string LiveFilePath { get; set; } = "/tmp/ready";

    /// <summary>
    /// Health check interval in seconds.
    /// </summary>
    public int IntervalSeconds { get; set; } = 30;
}