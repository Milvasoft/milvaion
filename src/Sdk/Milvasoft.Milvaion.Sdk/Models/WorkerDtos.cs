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
    /// Key: job type name, Value: JSON Schema of the job data type.
    /// </summary>
    public Dictionary<string, string> JobDataDefinitions { get; set; } = [];

    /// <summary>
    /// Job result definitions this worker handles.
    /// Key: job type name, Value: JSON Schema of the result type.
    /// Only present for jobs implementing IJobWithResult or IAsyncJobWithResult.
    /// </summary>
    public Dictionary<string, string> JobResultDefinitions { get; set; } = [];

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
    /// Physical memory usage (working set) of this worker instance's process, in bytes.
    /// </summary>
    public long MemoryBytes { get; set; }

    /// <summary>
    /// CPU usage percentage of this worker instance's process, normalized across all cores
    /// (0-100), measured over the interval between heartbeats.
    /// </summary>
    public double CpuUsagePercent { get; set; }

    /// <summary>
    /// Heartbeat timestamp.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Indicates that this worker instance is shutting down gracefully.
    /// When true, server should immediately cleanup consumer counts and running jobs.
    /// </summary>
    public bool IsStopping { get; set; }
}