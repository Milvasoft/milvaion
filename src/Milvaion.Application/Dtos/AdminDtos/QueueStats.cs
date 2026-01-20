using Milvaion.Application.Utils.Enums;

namespace Milvaion.Application.Dtos.AdminDtos;

/// <summary>
/// Detailed queue statistics
/// </summary>
public record QueueStats
{
    /// <summary>
    /// Queue name
    /// </summary>
    public string QueueName { get; init; }

    /// <summary>
    /// Current message count
    /// </summary>
    public uint MessageCount { get; init; }

    /// <summary>
    /// Number of consumers
    /// </summary>
    public uint ConsumerCount { get; init; }

    /// <summary>
    /// Messages ready for delivery
    /// </summary>
    public uint MessagesReady { get; init; }

    /// <summary>
    /// Messages unacknowledged
    /// </summary>
    public uint MessagesUnacknowledged { get; init; }

    /// <summary>
    /// Queue health status
    /// </summary>
    public QueueHealthStatus HealthStatus { get; init; }

    /// <summary>
    /// Timestamp of statistics
    /// </summary>
    public DateTime Timestamp { get; init; }
}