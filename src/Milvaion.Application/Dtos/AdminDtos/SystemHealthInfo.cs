using Milvaion.Application.Utils.Enums;

namespace Milvaion.Application.Dtos.AdminDtos;

/// <summary>
/// System health information
/// </summary>
public record SystemHealthInfo
{
    /// <summary>
    /// Whether job dispatcher is enabled
    /// </summary>
    public bool DispatcherEnabled { get; init; }

    /// <summary>
    /// Total number of active jobs
    /// </summary>
    public int TotalActiveJobs { get; init; }

    /// <summary>
    /// Queue statistics for all queues
    /// </summary>
    public List<QueueStats> QueueStats { get; init; }

    /// <summary>
    /// Overall system health status
    /// </summary>
    public SystemHealth OverallHealth { get; init; }

    /// <summary>
    /// Timestamp of health check
    /// </summary>
    public DateTime Timestamp { get; init; }
}