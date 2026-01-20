using Milvaion.Application.Utils.Enums;

namespace Milvaion.Application.Dtos.AdminDtos;

/// <summary>
/// Queue depth and health information.
/// </summary>
public record QueueDepthInfo
{
    /// <summary>
    /// Queue name
    /// </summary>
    public string QueueName { get; init; }

    /// <summary>
    /// Current message count in queue
    /// </summary>
    public uint MessageCount { get; init; }

    /// <summary>
    /// Number of active consumers
    /// </summary>
    public uint ConsumerCount { get; init; }

    /// <summary>
    /// Queue health status
    /// </summary>
    public QueueHealthStatus HealthStatus { get; init; }

    /// <summary>
    /// Health status message
    /// </summary>
    public string HealthMessage { get; init; }
}
