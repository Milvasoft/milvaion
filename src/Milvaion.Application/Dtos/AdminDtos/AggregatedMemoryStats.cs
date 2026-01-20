namespace Milvaion.Application.Dtos.AdminDtos;

/// <summary>
/// Memory tracking statistics for a background service.
/// </summary>
public class MemoryTrackStats
{
    /// <summary>
    /// Name of the service.
    /// </summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>
    /// Initial memory when service started (bytes).
    /// </summary>
    public long InitialMemoryBytes { get; set; }

    /// <summary>
    /// Current memory usage (bytes).
    /// </summary>
    public long CurrentMemoryBytes { get; set; }

    /// <summary>
    /// Last recorded memory usage (bytes).
    /// </summary>
    public long LastMemoryBytes { get; set; }

    /// <summary>
    /// Total memory growth since start (bytes).
    /// </summary>
    public long TotalGrowthBytes { get; set; }

    /// <summary>
    /// Current process memory usage (bytes).
    /// </summary>
    public long ProcessMemoryBytes { get; set; }

    /// <summary>
    /// Service start time.
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// Last memory check time.
    /// </summary>
    public DateTime LastCheckTime { get; set; }

    /// <summary>
    /// Whether potential memory leak is detected.
    /// </summary>
    public bool PotentialMemoryLeak { get; set; }

    /// <summary>
    /// Whether service is currently running.
    /// </summary>
    public bool IsRunning { get; set; }

    /// <summary>
    /// GC Gen0 collection count.
    /// </summary>
    public int Gen0Collections { get; set; }

    /// <summary>
    /// GC Gen1 collection count.
    /// </summary>
    public int Gen1Collections { get; set; }

    /// <summary>
    /// GC Gen2 collection count.
    /// </summary>
    public int Gen2Collections { get; set; }

    /// <summary>
    /// Initial memory when service started (megabytes).
    /// </summary>
    public double InitialMemoryMB => InitialMemoryBytes / 1024.0 / 1024.0;

    /// <summary>
    /// Current memory usage (megabytes).
    /// </summary>
    public double CurrentMemoryMB => CurrentMemoryBytes / 1024.0 / 1024.0;

    /// <summary>
    /// Last recorded memory usage (megabytes).
    /// </summary>
    public double LastMemoryMB => LastMemoryBytes / 1024.0 / 1024.0;

    /// <summary>
    /// Total memory growth since start (megabytes).
    /// </summary>
    public double TotalGrowthMB => TotalGrowthBytes / 1024.0 / 1024.0;

    /// <summary>
    /// Process memory usage (megabytes).
    /// </summary>
    public double ProcessMemoryMB => ProcessMemoryBytes / 1024.0 / 1024.0;

    /// <summary>
    /// Uptime of the service.
    /// </summary>
    public TimeSpan Uptime => DateTime.UtcNow - StartTime;
}

/// <summary>
/// Aggregated memory stats for all tracked services.
/// </summary>
public class AggregatedMemoryStats
{
    /// <summary>
    /// Timmestamp of the stats.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Total managed memory in bytes.
    /// </summary>
    public long TotalManagedMemoryBytes { get; set; }

    /// <summary>
    /// Total process memory in bytes.
    /// </summary>
    public long TotalProcessMemoryBytes { get; set; }

    /// <summary>
    /// Number of currently running services.
    /// </summary>
    public int RunningServicesCount { get; set; }

    /// <summary>
    /// Number of services with potential memory leaks detected.
    /// </summary>
    public int ServicesWithPotentialLeaks { get; set; }

    /// <summary>
    /// GC Gen0 collection count.
    /// </summary>
    public int Gen0Collections { get; set; }

    /// <summary>
    /// GC Gen1 collection count.
    /// </summary>
    public int Gen1Collections { get; set; }

    /// <summary>
    /// GC Gen2 collection count.
    /// </summary>
    public int Gen2Collections { get; set; }

    /// <summary>
    /// Memory statistics for each tracked service.
    /// </summary>
    public List<MemoryTrackStats> ServiceStats { get; set; } = [];

    /// <summary>
    /// Total managed memory in megabytes.
    /// </summary>
    public double TotalManagedMemoryMB => TotalManagedMemoryBytes / 1024.0 / 1024.0;

    /// <summary>
    /// Total process memory in megabytes.
    /// </summary>
    public double TotalProcessMemoryMB => TotalProcessMemoryBytes / 1024.0 / 1024.0;
}