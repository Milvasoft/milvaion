namespace Milvasoft.Milvaion.Sdk.Worker.Options;

/// <summary>
/// Configuration options for worker.
/// </summary>
public class WorkerOptions
{
    public const string SectionKey = "Worker";

    /// <summary>
    /// Unique identifier for this worker (app-level, user-defined).
    /// Example: "test-worker", "email-worker".
    /// Same across all replicas/instances.
    /// </summary>
    public string WorkerId { get; set; } = Environment.MachineName;

    /// <summary>
    /// Unique instance identifier (auto-generated).
    /// Format: {WorkerId}-{shortGuid}
    /// Different for each replica/instance.
    /// Example: "sample-worker-4817786d"
    /// </summary>
    public string InstanceId { get; private set; }

    /// <summary>
    /// Maximum parallel jobs this instance can run simultaneously.
    /// Default: ProcessorCount * 2 (e.g., 8 cores = 16 parallel jobs).
    /// </summary>
    public int MaxParallelJobs { get; set; } = Environment.ProcessorCount * 2;

    /// <summary>
    /// Default maximum execution time allowed for jobs (in seconds).
    /// If a job exceeds this timeout, it will be cancelled and marked as TimedOut.
    /// Default: 3600 seconds (1 hour).
    /// Set to 0 or negative value for no timeout (not recommended).
    /// Can be overridden per job consumer in JobConsumerOptions.
    /// </summary>
    public int ExecutionTimeoutSeconds { get; set; } = 3600; // 1 hour default

    /// <summary>
    /// Health check options.
    /// </summary>
    public HealthCheckOptions HealthCheck { get; set; } = new();

    /// <summary>
    /// RabbitMQ connection settings.
    /// </summary>
    public RabbitMQSettings RabbitMQ { get; set; } = new();

    /// <summary>
    /// Redis connection settings.
    /// </summary>
    public RedisSettings Redis { get; set; } = new();

    /// <summary>
    /// Heartbeat settings.
    /// </summary>
    public HeartbeatSettings Heartbeat { get; set; } = new();

    /// <summary>
    /// Offline resilience settings for local state persistence and sync.
    /// </summary>
    public OfflineResilienceSettings OfflineResilience { get; set; } = new();

    /// <summary>
    /// Initializes WorkerOptions.
    /// Note: InstanceId is generated after WorkerId is set from configuration.
    /// </summary>
    public WorkerOptions()
    {
        // Don't call RegenerateInstanceId here!
        // It will use default WorkerId (Environment.MachineName) instead of configured value
    }

    /// <summary>
    /// Generates a unique InstanceId using WorkerId, container hostname, and a short GUID.
    /// Format: {WorkerId}-{shortGuid}
    /// Example: test-worker-4817786d
    /// Uses timestamp-based GUID (Version 7) combined with container-specific entropy for uniqueness.
    /// </summary>
    public void RegenerateInstanceId()
    {
        // Generate a truly unique instance ID using:
        // 1. Timestamp (Guid.CreateVersion7 - sortable, time-based)
        // 2. Machine/Container name entropy
        // 3. Process ID entropy
        var timeBasedGuid = Guid.CreateVersion7();
        var machineName = Environment.MachineName;
        var processId = Environment.ProcessId;

        // Combine machine name and process ID for additional entropy
        var entropy = $"{machineName}-{processId}";
        var entropyHash = entropy.GetHashCode();

        // XOR the GUID with entropy hash to ensure uniqueness across containers
        var guidBytes = timeBasedGuid.ToByteArray();
        var entropyBytes = BitConverter.GetBytes(entropyHash);

        for (var i = 0; i < Math.Min(guidBytes.Length, entropyBytes.Length); i++)
            guidBytes[i] ^= entropyBytes[i % entropyBytes.Length];

        var uniqueGuid = new Guid(guidBytes);
        var shortGuid = uniqueGuid.ToString("N")[..8];

        // Final format: WorkerId-shortGuid
        InstanceId = $"{WorkerId}-{shortGuid}";
    }

    /// <summary>
    /// Sets the InstanceId explicitly (used when copying from base options).
    /// </summary>
    /// <param name="instanceId">The instance ID to set</param>
    public void SetInstanceId(string instanceId) => InstanceId = instanceId;
}