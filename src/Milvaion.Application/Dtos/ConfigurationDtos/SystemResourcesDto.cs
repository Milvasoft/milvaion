namespace Milvaion.Application.Dtos.ConfigurationDtos;

/// <summary>
/// System resources information.
/// </summary>
public class SystemResourcesDto
{
    /// <summary>
    /// CPU usage percentage (0-100).
    /// </summary>
    public double CpuUsagePercent { get; set; }

    /// <summary>
    /// Total physical memory in MB.
    /// </summary>
    public long TotalMemoryMB { get; set; }

    /// <summary>
    /// Used memory in MB.
    /// </summary>
    public long UsedMemoryMB { get; set; }

    /// <summary>
    /// Available memory in MB.
    /// </summary>
    public long AvailableMemoryMB { get; set; }

    /// <summary>
    /// Memory usage percentage (0-100).
    /// </summary>
    public double MemoryUsagePercent { get; set; }

    /// <summary>
    /// Process memory usage in MB.
    /// </summary>
    public long ProcessMemoryMB { get; set; }

    /// <summary>
    /// Total disk space in GB.
    /// </summary>
    public long TotalDiskGB { get; set; }

    /// <summary>
    /// Available disk space in GB.
    /// </summary>
    public long AvailableDiskGB { get; set; }

    /// <summary>
    /// Disk usage percentage (0-100).
    /// </summary>
    public double DiskUsagePercent { get; set; }
}
