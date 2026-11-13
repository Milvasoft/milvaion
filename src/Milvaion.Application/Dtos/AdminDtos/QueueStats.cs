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
    /// Messages delivered but not acknowledged yet. Null when the transport cannot report it (Kafka).
    /// </summary>
    public uint? MessagesUnacknowledged { get; init; }

    /// <summary>
    /// Queue health status
    /// </summary>
    public QueueHealthStatus HealthStatus { get; init; }

    /// <summary>
    /// Transport the figures come from ("Kafka"). Null on RabbitMQ. On Kafka the queue is a topic and the
    /// message count is the consumer lag of the group that reads it.
    /// </summary>
    public string Transport { get; init; }

    /// <summary>
    /// Explains the figures, or why they are not available.
    /// </summary>
    public string Note { get; init; }

    /// <summary>
    /// Timestamp of statistics
    /// </summary>
    public DateTime Timestamp { get; init; }
}