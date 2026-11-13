using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Milvaion.Application.Dtos.AdminDtos;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.Enums;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Helpers;
using Milvasoft.Milvaion.Sdk.Utils;
using Milvasoft.Milvaion.Sdk.Utils.Kafka;

namespace Milvaion.Infrastructure.Services.Kafka;

/// <summary>
/// Queue statistics on Kafka: one entry per Milvaion topic, with the consumer lag of the group that reads it as the depth.
/// </summary>
public sealed class KafkaQueueDepthMonitor(KafkaClientFactory clientFactory, KafkaLagReader lagReader, ILoggerFactory loggerFactory) : IQueueDepthMonitor
{
    private const string _transport = "Kafka";

    private readonly KafkaClientFactory _clientFactory = clientFactory;
    private readonly KafkaLagReader _lagReader = lagReader;
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<KafkaQueueDepthMonitor>();

    /// <inheritdoc/>
    public async Task<QueueDepthInfo> GetQueueDepthAsync(string queueName, CancellationToken cancellationToken = default)
    {
        var stats = await GetDetailedStatsAsync(queueName, cancellationToken);

        return new QueueDepthInfo
        {
            QueueName = stats.QueueName,
            MessageCount = stats.MessageCount,
            ConsumerCount = stats.ConsumerCount,
            HealthStatus = stats.HealthStatus,
            HealthMessage = stats.Note ?? GetHealthMessage(stats.MessageCount, stats.HealthStatus),
            Transport = _transport
        };
    }

    /// <inheritdoc/>
    public async Task<bool> IsQueueHealthyAsync(string queueName, CancellationToken cancellationToken = default)
    {
        var info = await GetQueueDepthAsync(queueName, cancellationToken);

        return info.HealthStatus is not QueueHealthStatus.Critical and not QueueHealthStatus.Unavailable;
    }

    /// <inheritdoc/>
    public async Task<QueueStats> GetDetailedStatsAsync(string queueName, CancellationToken cancellationToken = default)
    {
        // Accepts a topic name, or the RabbitMQ queue name of the same traffic.
        var topic = KafkaTopicNames.ForQueue(_clientFactory.Prefix, queueName) ?? queueName;

        try
        {
            var lag = await _lagReader.GetLagAsync(topic, GroupOf(topic), cancellationToken);

            if (lag == null)
                return Unavailable(topic, "Topic does not exist.");

            var count = (uint)Math.Min(lag.Lag, uint.MaxValue);

            return new QueueStats
            {
                QueueName = topic,
                MessageCount = count,
                MessagesReady = count,
                ConsumerCount = (uint)(lag.Consumers ?? 0),
                MessagesUnacknowledged = null,
                HealthStatus = DetermineHealthStatus(lag.Lag),
                Transport = _transport,
                Note = lag.Consumers == null
                    ? $"Consumer lag of group {lag.Group}. Consumer count not available."
                    : $"Consumer lag of group {lag.Group}.",
                Timestamp = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to read Kafka lag for {Topic}", topic);

            return Unavailable(topic, $"Lag not available: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public async Task<List<QueueStats>> GetAllQueueStatsAsync(CancellationToken cancellationToken = default)
    {
        List<string> topics;

        try
        {
            var stem = KafkaTopicNames.WithPrefix(_clientFactory.Prefix, KafkaTopicNames.Root + ".");

            topics = [.. _clientFactory.GetMetadata()
                                       .Topics
                                       .Where(t => t.Error.Code == ErrorCode.NoError && t.Topic.StartsWith(stem, StringComparison.Ordinal))
                                       .Select(t => t.Topic)
                                       .OrderBy(t => t, StringComparer.Ordinal)];
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to list Kafka topics");

            return [.. KafkaTopicNames.SharedTopics(_clientFactory.Prefix).Select(t => Unavailable(t, $"Lag not available: {ex.Message}"))];
        }

        var results = await Task.WhenAll(topics.Select(topic => GetDetailedStatsAsync(topic, cancellationToken)));

        return [.. results];
    }

    private string GroupOf(string topic)
    {
        var worker = KafkaTopicNames.WorkerOfJobTopic(_clientFactory.Prefix, topic);

        return worker != null
            ? KafkaTopicNames.WorkerGroup(_clientFactory.Prefix, worker)
            : KafkaTopicNames.WithPrefix(_clientFactory.Prefix, KafkaTopicNames.ApiGroup);
    }

    private QueueHealthStatus DetermineHealthStatus(long lag)
    {
        if (lag >= _clientFactory.Options.LagCriticalThreshold)
            return QueueHealthStatus.Critical;

        if (lag >= _clientFactory.Options.LagWarningThreshold)
            return QueueHealthStatus.Warning;

        return QueueHealthStatus.Healthy;
    }

    private string GetHealthMessage(uint lag, QueueHealthStatus status) => status switch
    {
        QueueHealthStatus.Healthy => "Topic consumed normally",
        QueueHealthStatus.Warning => $"Consumer lag elevated: {lag}/{_clientFactory.Options.LagWarningThreshold} (warning threshold)",
        QueueHealthStatus.Critical => $"Consumer lag critical: {lag}/{_clientFactory.Options.LagCriticalThreshold} (critical threshold)",
        _ => "Lag not available"
    };

    private static QueueStats Unavailable(string topic, string note) => new()
    {
        QueueName = topic,
        HealthStatus = QueueHealthStatus.Unavailable,
        MessagesUnacknowledged = null,
        Transport = _transport,
        Note = note,
        Timestamp = DateTime.UtcNow
    };
}
