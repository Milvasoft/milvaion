using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Dtos.AdminDtos;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.Enums;
using Milvaion.Infrastructure.Services.RabbitMQ;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Utils;

namespace Milvaion.Infrastructure.Services.Monitoring;

/// <summary>
/// Implementation of queue depth monitoring service.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="QueueDepthMonitor"/> class.
/// </remarks>
public class QueueDepthMonitor(RabbitMQConnectionFactory connectionFactory, IOptions<RabbitMQOptions> options, ILoggerFactory loggerFactory) : IQueueDepthMonitor
{
    private readonly RabbitMQConnectionFactory _connectionFactory = connectionFactory;
    private readonly RabbitMQOptions _options = options.Value;
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<QueueDepthMonitor>();

    /// <inheritdoc/>
    public async Task<QueueDepthInfo> GetQueueDepthAsync(string queueName, CancellationToken cancellationToken = default)
    {
        try
        {
            var channel = await _connectionFactory.GetChannelAsync(cancellationToken);

            // Use QueueDeclare with passive=true instead of QueueDeclarePassive
            var queueInfo = await channel.QueueDeclarePassiveAsync(queueName, cancellationToken);

            var healthStatus = DetermineHealthStatus(queueInfo.MessageCount);

            var healthMessage = GetHealthMessage(queueInfo.MessageCount, healthStatus);

            return new QueueDepthInfo
            {
                QueueName = queueName,
                MessageCount = queueInfo.MessageCount,
                ConsumerCount = queueInfo.ConsumerCount,
                HealthStatus = healthStatus,
                HealthMessage = healthMessage
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to get queue depth for {QueueName}", queueName);

            return new QueueDepthInfo
            {
                QueueName = queueName,
                MessageCount = 0,
                ConsumerCount = 0,
                HealthStatus = QueueHealthStatus.Unavailable,
                HealthMessage = $"Queue unavailable: {ex.Message}"
            };
        }
    }

    /// <inheritdoc/>
    public async Task<bool> IsQueueHealthyAsync(string queueName, CancellationToken cancellationToken = default)
    {
        var info = await GetQueueDepthAsync(queueName, cancellationToken);

        if (info.HealthStatus == QueueHealthStatus.Critical)
        {
            _logger.Error("Queue {QueueName} at CRITICAL level: {MessageCount} messages", queueName, info.MessageCount);
            return false;
        }

        if (info.HealthStatus == QueueHealthStatus.Warning)
        {
            _logger.Warning("Queue {QueueName} at WARNING level: {MessageCount} messages", queueName, info.MessageCount);
        }

        return info.HealthStatus != QueueHealthStatus.Unavailable;
    }

    /// <inheritdoc/>
    public async Task<QueueStats> GetDetailedStatsAsync(string queueName, CancellationToken cancellationToken = default)
    {
        try
        {
            var channel = await _connectionFactory.GetChannelAsync(cancellationToken);
            var queueInfo = await channel.QueueDeclarePassiveAsync(queueName, cancellationToken);

            var healthStatus = DetermineHealthStatus(queueInfo.MessageCount);

            return new QueueStats
            {
                QueueName = queueName,
                MessageCount = queueInfo.MessageCount,
                ConsumerCount = queueInfo.ConsumerCount,
                MessagesReady = queueInfo.MessageCount, // Simplified - all messages ready
                MessagesUnacknowledged = 0, // Would need RabbitMQ Management API for this
                HealthStatus = healthStatus,
                Timestamp = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to get detailed stats for {QueueName}", queueName);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<List<QueueStats>> GetAllQueueStatsAsync(CancellationToken cancellationToken = default)
    {
        // TODO: This only monitors the main queues defined in RabbitMQOptions.
        // Consumer routing key queues (e.g., scheduled_jobs_queue.SampleWorker) are created
        // automatically by RabbitMQ when workers bind to the exchange with routing keys.
        //
        // To monitor these dynamic queues, RabbitMQ Management API integration would be needed:
        // - GET /api/queues/{vhost} to list all queues
        // - Filter queues by pattern (e.g., starts with main queue name)
        //
        // For now, this monitors the 3 core queues which is sufficient for basic health checks.

        var queueNames = new[]
        {
            WorkerConstant.Queues.Jobs,
            WorkerConstant.Queues.WorkerLogs,
            WorkerConstant.Queues.WorkerHeartbeat,
            WorkerConstant.Queues.WorkerRegistration,
            WorkerConstant.Queues.StatusUpdates,
            WorkerConstant.Queues.FailedOccurrences
        };

        var tasks = queueNames.Select(queueName => GetDetailedStatsAsync(queueName, cancellationToken));
        var results = await Task.WhenAll(tasks);

        return [.. results];
    }

    private QueueHealthStatus DetermineHealthStatus(uint messageCount)
    {
        if (messageCount >= _options.QueueDepthCriticalThreshold)
            return QueueHealthStatus.Critical;

        if (messageCount >= _options.QueueDepthWarningThreshold)
            return QueueHealthStatus.Warning;

        return QueueHealthStatus.Healthy;
    }

    private string GetHealthMessage(uint messageCount, QueueHealthStatus status) => status switch
    {
        QueueHealthStatus.Healthy => "Queue operating normally",
        QueueHealthStatus.Warning => $"Queue depth elevated: {messageCount}/{_options.QueueDepthWarningThreshold} (warning threshold)",
        QueueHealthStatus.Critical => $"Queue at critical capacity: {messageCount}/{_options.QueueDepthCriticalThreshold} (critical threshold)",
        QueueHealthStatus.Unavailable => "Queue is unavailable",
        _ => "Unknown status"
    };
}
