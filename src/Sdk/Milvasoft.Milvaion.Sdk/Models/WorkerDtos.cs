namespace Milvasoft.Milvaion.Sdk.Models;

/// <summary>
/// Worker registration request sent by workers on startup.
/// </summary>
public class WorkerDiscoveryRequest
{
    /// <summary>
    /// Unique worker identifier.
    /// </summary>
    public string WorkerId { get; set; }

    /// <summary>
    /// Unique instance identifier (auto-generated).
    /// Format: {WorkerId}-{shortGuid}
    /// </summary>
    public string InstanceId { get; set; }

    /// <summary>
    /// Friendly display name.
    /// </summary>
    public string DisplayName { get; set; }

    /// <summary>
    /// Worker hostname.
    /// </summary>
    public string HostName { get; set; }

    /// <summary>
    /// Worker IP address.
    /// </summary>
    public string IpAddress { get; set; }

    /// <summary>
    /// Routing patterns this worker handles.
    /// </summary>
    public Dictionary<string, string> RoutingPatterns { get; set; } = [];

    /// <summary>
    /// Job data definitions this worker handles.
    /// </summary>
    public Dictionary<string, string> JobDataDefinitions { get; set; } = [];

    /// <summary>
    /// Job types this worker can execute.
    /// </summary>
    public List<string> JobTypes { get; set; } = [];

    /// <summary>
    /// Maximum parallel jobs this instance can run simultaneously.
    /// </summary>
    public int MaxParallelJobs { get; set; }

    /// <summary>
    /// Worker version.
    /// </summary>
    public string Version { get; set; }

    /// <summary>
    /// Additional metadata.
    /// </summary>
    public string Metadata { get; set; }
}

/// <summary>
/// Worker heartbeat message.
/// </summary>
public class WorkerHeartbeatMessage
{
    /// <summary>
    /// Worker group identifier.
    /// </summary>
    public string WorkerId { get; set; }

    /// <summary>
    /// Unique instance identifier.
    /// </summary>
    public string InstanceId { get; set; }

    /// <summary>
    /// Current number of jobs being processed by this instance.
    /// </summary>
    public int CurrentJobs { get; set; }

    /// <summary>
    /// Heartbeat timestamp.
    /// </summary>
    public DateTime Timestamp { get; set; }
}