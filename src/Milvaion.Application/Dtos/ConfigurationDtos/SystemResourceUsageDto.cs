namespace Milvaion.Application.Dtos.ConfigurationDtos;

/// <summary>
/// Live resource usage of the API host and process.
/// </summary>
/// <remarks>
/// A superset of <see cref="SystemResourcesDto"/>, which stays as it is because it is part of the
/// configuration payload the dashboard already consumes. This one carries the extra fields worth having when the
/// question is specifically "how much memory is this using" rather than "what is this configured to do":
/// the process working set alongside the managed heap, the garbage collector's own accounting, and the thread
/// count.
///
/// Reported by a query that touches nothing but the current process, so it is cheap enough to poll. The system
/// configuration endpoint returns this too, but only as one branch of a payload that also reads every
/// configuration section, the connection strings and the alerting channels - too much to fetch when all that is
/// wanted is a memory figure.
/// </remarks>
public class SystemResourceUsageDto
{
    /// <summary>
    /// UTC time the sample was taken.
    /// </summary>
    public DateTime SampledAt { get; set; }

    /// <summary>
    /// Machine or container name the API is running on.
    /// </summary>
    public string HostName { get; set; }

    /// <summary>
    /// Logical processors visible to the process.
    /// </summary>
    public int ProcessorCount { get; set; }

    /// <summary>
    /// Average CPU usage of this process since it started, as a percentage of one machine's worth of CPU.
    /// </summary>
    /// <remarks>
    /// An average over the process lifetime, not an instantaneous reading: it is derived from total processor
    /// time divided by wall clock time. A process that was busy an hour ago and idle now still reports a high
    /// figure, so this answers "has this process been working hard" rather than "is it working hard right now".
    /// </remarks>
    public double CpuUsagePercent { get; set; }

    /// <summary>
    /// Memory the runtime considers available to the process, in MB.
    /// </summary>
    /// <remarks>
    /// From the garbage collector, so under a container memory limit this reflects the limit rather than the
    /// physical memory of the host - which is the number that matters when the container is the thing being
    /// killed for using too much.
    /// </remarks>
    public long TotalMemoryMB { get; set; }

    /// <summary>
    /// Managed heap in use, in MB.
    /// </summary>
    public long UsedMemoryMB { get; set; }

    /// <summary>
    /// Memory available to the process, in MB.
    /// </summary>
    public long AvailableMemoryMB { get; set; }

    /// <summary>
    /// Managed heap as a percentage of what is available.
    /// </summary>
    public double MemoryUsagePercent { get; set; }

    /// <summary>
    /// Resident memory of the whole process, in MB.
    /// </summary>
    /// <remarks>
    /// The figure an operating system or an orchestrator reports, and the one a memory limit is enforced
    /// against. Always larger than the managed heap: it also contains the runtime itself, native allocations,
    /// loaded assemblies and thread stacks. A gap that keeps widening while the heap stays flat points at a
    /// native leak rather than a managed one.
    /// </remarks>
    public long ProcessMemoryMB { get; set; }

    /// <summary>
    /// Largest resident memory this process has reached, in MB.
    /// </summary>
    public long PeakProcessMemoryMB { get; set; }

    /// <summary>
    /// Bytes the garbage collector has allocated over the process lifetime, in MB.
    /// </summary>
    /// <remarks>
    /// Cumulative and only ever rising. Useful as a rate - allocation per hour of uptime - rather than as a
    /// level; a high number on a long-lived process is normal.
    /// </remarks>
    public long TotalAllocatedMB { get; set; }

    /// <summary>
    /// Collections performed in each garbage collector generation.
    /// </summary>
    /// <remarks>
    /// Gen 2 is the one to watch. Frequent gen 2 collections mean objects are surviving long enough to be
    /// promoted, which is what memory pressure looks like before it becomes an out-of-memory failure.
    /// </remarks>
    public int Gen0Collections { get; set; }

    /// <inheritdoc cref="Gen0Collections"/>
    public int Gen1Collections { get; set; }

    /// <inheritdoc cref="Gen0Collections"/>
    public int Gen2Collections { get; set; }

    /// <summary>
    /// Threads owned by the process.
    /// </summary>
    public int ThreadCount { get; set; }

    /// <summary>
    /// How long the process has been running.
    /// </summary>
    public TimeSpan Uptime { get; set; }

    /// <summary>
    /// Total fixed disk space visible to the host, in GB.
    /// </summary>
    public long TotalDiskGB { get; set; }

    /// <summary>
    /// Free fixed disk space, in GB.
    /// </summary>
    public long AvailableDiskGB { get; set; }

    /// <summary>
    /// Used disk space as a percentage of the total.
    /// </summary>
    public double DiskUsagePercent { get; set; }

    /// <summary>
    /// Set when the sample could not be collected, explaining why the figures are zero.
    /// </summary>
    /// <remarks>
    /// Reading process counters and drive information can be denied in a hardened container. Saying so is worth
    /// more than silently returning zeros, which read as "nothing is being used".
    /// </remarks>
    public string CollectionError { get; set; }
}
