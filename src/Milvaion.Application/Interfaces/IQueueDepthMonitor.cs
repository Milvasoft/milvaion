using Milvaion.Application.Dtos.AdminDtos;

namespace Milvaion.Application.Interfaces;

/// <summary>
/// Service for monitoring RabbitMQ queue depth and health.
/// </summary>
public interface IQueueDepthMonitor
{
    /// <summary>
    /// Gets current queue depth information.
    /// </summary>
    Task<QueueDepthInfo> GetQueueDepthAsync(string queueName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if queue is healthy based on depth thresholds.
    /// </summary>
    Task<bool> IsQueueHealthyAsync(string queueName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets detailed queue statistics.
    /// </summary>
    Task<QueueStats> GetDetailedStatsAsync(string queueName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets statistics for all configured queues.
    /// </summary>
    Task<List<QueueStats>> GetAllQueueStatsAsync(CancellationToken cancellationToken = default);
}