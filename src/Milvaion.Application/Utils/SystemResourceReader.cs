using Milvaion.Application.Dtos.ConfigurationDtos;
using System.Diagnostics;

namespace Milvaion.Application.Utils;

/// <summary>
/// Reads live CPU, memory and disk usage of the API process and its host.
/// </summary>
/// <remarks>
/// Extracted so there is one implementation rather than two. The system configuration handler had this inline;
/// the resource usage query needs the same numbers, and a second copy would have drifted the first time either
/// was corrected.
///
/// Every figure comes from the current process or from the drives already mounted. There is no database call,
/// no broker round trip and no cache, which is what makes the resource usage endpoint cheap enough to poll -
/// unlike the configuration endpoint, which returns this as one branch of a payload that also reads every
/// configuration section.
/// </remarks>
public static class SystemResourceReader
{
    private const long _bytesPerMB = 1024L * 1024L;
    private const long _bytesPerGB = 1024L * 1024L * 1024L;

    /// <summary>
    /// Takes a sample of the current process and host.
    /// </summary>
    /// <returns>
    /// A populated sample, or one with every figure at zero and <see cref="SystemResourceUsageDto.CollectionError"/>
    /// set. It never throws: this is called from a query handler on a monitoring path, and a hardened container
    /// that denies access to process counters should degrade the answer rather than fail the request.
    /// </returns>
    public static SystemResourceUsageDto Read()
    {
        try
        {
            using var process = Process.GetCurrentProcess();

            var gcInfo = GC.GetGCMemoryInfo();

            // `TotalAvailableMemoryBytes` respects a container memory limit where physical memory does not, so
            // under Docker or Kubernetes this is the ceiling the process is actually judged against.
            var totalMemoryBytes = gcInfo.TotalAvailableMemoryBytes;
            var usedMemoryBytes = GC.GetTotalMemory(forceFullCollection: false);

            var totalMemoryMB = totalMemoryBytes / _bytesPerMB;
            var usedMemoryMB = usedMemoryBytes / _bytesPerMB;

            var startedAt = process.StartTime.ToUniversalTime();
            var uptime = DateTime.UtcNow - startedAt;

            // Total processor time over wall clock time, divided by the core count so the result is a
            // percentage of one machine rather than of one core. An average over the process lifetime - see the
            // remarks on the DTO property.
            var cpuPercent = uptime.TotalMilliseconds > 0
                ? process.TotalProcessorTime.TotalMilliseconds / uptime.TotalMilliseconds / Environment.ProcessorCount * 100
                : 0;

            var (totalDiskGB, availableDiskGB) = ReadDisk();

            return new SystemResourceUsageDto
            {
                SampledAt = DateTime.UtcNow,
                HostName = Environment.MachineName,
                ProcessorCount = Environment.ProcessorCount,

                CpuUsagePercent = Math.Round(Math.Clamp(cpuPercent, 0, 100), 2),

                TotalMemoryMB = totalMemoryMB,
                UsedMemoryMB = usedMemoryMB,
                AvailableMemoryMB = Math.Max(0, totalMemoryMB - usedMemoryMB),
                MemoryUsagePercent = totalMemoryMB > 0 ? Math.Round((double)usedMemoryMB / totalMemoryMB * 100, 2) : 0,

                ProcessMemoryMB = process.WorkingSet64 / _bytesPerMB,
                PeakProcessMemoryMB = process.PeakWorkingSet64 / _bytesPerMB,
                TotalAllocatedMB = GC.GetTotalAllocatedBytes(precise: false) / _bytesPerMB,

                Gen0Collections = GC.CollectionCount(0),
                Gen1Collections = GC.CollectionCount(1),
                Gen2Collections = GC.CollectionCount(2),

                ThreadCount = process.Threads.Count,
                Uptime = uptime,

                TotalDiskGB = totalDiskGB,
                AvailableDiskGB = availableDiskGB,
                DiskUsagePercent = totalDiskGB > 0
                    ? Math.Round((double)(totalDiskGB - availableDiskGB) / totalDiskGB * 100, 2)
                    : 0
            };
        }
        catch (Exception exception)
        {
            // Named rather than swallowed. Zeros with no explanation read as "nothing is being used", which is
            // the opposite of what a denied counter means.
            return new SystemResourceUsageDto
            {
                SampledAt = DateTime.UtcNow,
                HostName = Environment.MachineName,
                CollectionError = exception.Message
            };
        }
    }

    /// <summary>
    /// Maps a sample onto the shape the system configuration payload has always returned.
    /// </summary>
    /// <remarks>
    /// Kept so the configuration endpoint's response is byte for byte what it was. Changing a payload the
    /// dashboard already reads is not part of adding a new one.
    /// </remarks>
    public static SystemResourcesDto ToSystemResources(SystemResourceUsageDto usage) => new()
    {
        CpuUsagePercent = usage.CpuUsagePercent,
        TotalMemoryMB = usage.TotalMemoryMB,
        UsedMemoryMB = usage.UsedMemoryMB,
        AvailableMemoryMB = usage.AvailableMemoryMB,
        MemoryUsagePercent = usage.MemoryUsagePercent,
        ProcessMemoryMB = usage.ProcessMemoryMB,
        TotalDiskGB = usage.TotalDiskGB,
        AvailableDiskGB = usage.AvailableDiskGB,
        DiskUsagePercent = usage.DiskUsagePercent
    };

    /// <summary>
    /// Totals the fixed, ready drives.
    /// </summary>
    /// <remarks>
    /// Enumerating drives can throw for a single unreadable mount, which would otherwise lose the memory
    /// figures alongside the disk ones. Isolated here so a disk problem costs only the disk numbers.
    /// </remarks>
    private static (long TotalGB, long AvailableGB) ReadDisk()
    {
        try
        {
            var drives = DriveInfo.GetDrives().Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed).ToList();

            if (drives.Count == 0)
                return (0, 0);

            return (drives.Sum(drive => drive.TotalSize) / _bytesPerGB,
                    drives.Sum(drive => drive.AvailableFreeSpace) / _bytesPerGB);
        }
        catch
        {
            return (0, 0);
        }
    }
}
