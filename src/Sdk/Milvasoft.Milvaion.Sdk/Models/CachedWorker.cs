using Milvasoft.Milvaion.Sdk.Domain.Enums;

namespace Milvasoft.Milvaion.Sdk.Models;

/// <summary>
/// Redis-cached worker model with runtime state.
/// Separate from database entity for clear separation of concerns.
/// </summary>
public class CachedWorker
{
    /// <summary>
    /// Unique worker identifier.
    /// </summary>
    public string WorkerId { get; set; }

    /// <summary>
    /// Friendly display name.
    /// </summary>
    public string DisplayName { get; set; }

    /// <summary>
    /// Job name and routing patterns pair this worker handles.
    /// </summary>
    public Dictionary<string, string> RoutingPatterns { get; set; } = [];

    /// <summary>
    /// Job name and jobdata definition pair this worker handles.
    /// </summary>
    public Dictionary<string, string> JobDataDefinitions { get; set; } = [];

    /// <summary>
    /// Job name and result schema definition pair this worker handles.
    /// Only present for jobs implementing IJobWithResult or IAsyncJobWithResult.
    /// </summary>
    public Dictionary<string, string> JobResultDefinitions { get; set; } = [];

    /// <summary>
    /// Job types this worker can execute.
    /// </summary>
    public List<string> JobNames { get; set; } = [];

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
    public WorkerMetadata Metadata { get; set; }

    /// <summary>
    /// When the worker first registered.
    /// </summary>
    public DateTime RegisteredAt { get; set; }

    /// <summary>
    /// Total current number of jobs being processed across all instances.
    /// </summary>
    public int CurrentJobs { get; set; }

    /// <summary>
    /// Total physical memory usage (working set) across all instances, in bytes.
    /// </summary>
    public long MemoryBytes { get; set; }

    /// <summary>
    /// Average CPU usage percentage across all instances (0-100).
    /// </summary>
    public double CpuUsagePercent { get; set; }

    /// <summary>
    /// Worker status (Active, Inactive, Zombie).
    /// </summary>
    public WorkerStatus Status { get; set; }

    /// <summary>
    /// Last heartbeat timestamp from any instance.
    /// </summary>
    public DateTime? LastHeartbeat { get; set; }

    /// <summary>
    /// Active instances (replicas) of this worker.
    /// </summary>
    public List<WorkerInstance> Instances { get; set; } = [];
}

/// <summary>
/// Model for worker instance stored as JSON (not separate entity).
/// </summary>
public class WorkerInstance
{
    /// <summary>
    /// Unique instance identifier.
    /// Format: {WorkerId}-{shortGuid} (e.g., "test-worker-abc12345")
    /// </summary>
    public string InstanceId { get; set; }

    /// <summary>
    /// Instance hostname or container name.
    /// </summary>
    public string HostName { get; set; }

    /// <summary>
    /// Instance IP address.
    /// </summary>
    public string IpAddress { get; set; }

    /// <summary>
    /// Current number of jobs being processed by this instance.
    /// </summary>
    public int CurrentJobs { get; set; }

    /// <summary>
    /// Physical memory usage (working set) of this instance's process, in bytes.
    /// </summary>
    public long MemoryBytes { get; set; }

    /// <summary>
    /// CPU usage percentage of this instance's process (0-100, normalized across cores).
    /// </summary>
    public double CpuUsagePercent { get; set; }

    /// <summary>
    /// Instance status (Active, Inactive, Zombie).
    /// </summary>
    public WorkerStatus Status { get; set; }

    /// <summary>
    /// Last heartbeat timestamp from this instance.
    /// </summary>
    public DateTime LastHeartbeat { get; set; }

    /// <summary>
    /// When this instance first registered.
    /// </summary>
    public DateTime RegisteredAt { get; set; }
}

/// <summary>
/// Worker metadata details.
/// </summary>
public class WorkerMetadata
{
    /// <summary>
    /// Determines if the worker is an external scheduler integration (e.g., Quartz).
    /// </summary>
    public bool IsExternal { get; set; }

    /// <summary>
    /// External scheduler type (e.g., "Quartz", "Hangfire") if IsExternal is true.
    /// </summary>
    public string ExternalScheduler { get; set; }

    /// <summary>
    /// Processor count of the worker machine.
    /// </summary>
    public int ProcessorCount { get; set; }

    /// <summary>
    /// OS version of the worker machine.
    /// </summary>
    public string OSVersion { get; set; }

    /// <summary>
    /// Runtime version (e.g., .NET 10.0) of the worker machine.
    /// </summary>
    public string RuntimeVersion { get; set; }

    /// <summary>
    /// Heartbeat interval in seconds configured for this worker.
    /// </summary>
    public int HeartbeatInterval { get; set; }

    /// <summary>
    /// Job configuration metadata for each job type this worker can execute.
    /// </summary>
    public List<JobConfigMetadata> JobConfigs { get; set; }
}

/// <summary>
/// Job consumer configuration metadata.
/// </summary>
public class JobConfigMetadata
{
    public string JobType { get; set; }
    public string ConsumerId { get; set; }
    public int MaxParallelJobs { get; set; }
    public int ExecutionTimeoutSeconds { get; set; }
}