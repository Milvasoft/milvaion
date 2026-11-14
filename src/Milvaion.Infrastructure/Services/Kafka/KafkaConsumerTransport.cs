using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Milvaion.Application.Interfaces.Messaging;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Helpers;
using Milvasoft.Milvaion.Sdk.Utils;
using Milvasoft.Milvaion.Sdk.Utils.Kafka;
using System.Collections.Concurrent;

namespace Milvaion.Infrastructure.Services.Kafka;

/// <summary>
/// Consumes the API's Kafka topics in the <c>milvaion.api</c> group, with commits that never pass an unsettled record.
/// </summary>
public sealed class KafkaConsumerTransport(KafkaClientFactory clientFactory, ILoggerFactory loggerFactory) : IMessageConsumerTransport
{
    private readonly KafkaClientFactory _clientFactory = clientFactory;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;

    /// <inheritdoc/>
    public string Name => "Kafka";

    /// <inheritdoc/>
    public Task<IMessageConsumer> StartConsumerAsync(ConsumerSubscription subscription, Func<IMessageDelivery, Task> handler, CancellationToken cancellationToken)
    {
        var topic = KafkaTopicNames.ForQueue(_clientFactory.Prefix, subscription.Queue)
                    ?? throw new InvalidOperationException($"Queue '{subscription.Queue}' has no Kafka topic.");

        // Fails fast when no broker answers, so the caller's reconnect loop backs off and alerts.
        _clientFactory.GetMetadata();

        var consumer = new KafkaMessageConsumer(_clientFactory, topic, subscription, handler, _loggerFactory.CreateMilvaLogger<KafkaMessageConsumer>());

        consumer.Start(cancellationToken);

        return Task.FromResult<IMessageConsumer>(consumer);
    }
}

/// <summary>
/// One Kafka consumer on a dedicated thread. Records are handed to the handler one at a time, like a RabbitMQ channel
/// with a single dispatch slot; they are settled later, possibly from other threads. Every partition keeps a
/// <see cref="PartitionOffsetTracker"/> and only the end of its contiguous settled range is committed.
/// </summary>
internal sealed class KafkaMessageConsumer : IMessageConsumer
{
    private static readonly TimeSpan _pollTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan _commitInterval = TimeSpan.FromMilliseconds(500);

    private readonly KafkaClientFactory _clientFactory;
    private readonly string _topic;
    private readonly ConsumerSubscription _subscription;
    private readonly Func<IMessageDelivery, Task> _handler;
    private readonly IMilvaLogger _logger;
    private readonly IConsumer<string, byte[]> _consumer;
    private readonly ConcurrentDictionary<TopicPartition, PartitionOffsetTracker> _trackers = new();
    private readonly TaskCompletionSource _died = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _commitLock = new();
    private Task _loop = Task.CompletedTask;
    private DateTime _lastCommit = DateTime.MinValue;
    private bool _paused;
    private int _disposed;

    public KafkaMessageConsumer(KafkaClientFactory clientFactory, string topic, ConsumerSubscription subscription, Func<IMessageDelivery, Task> handler, IMilvaLogger logger)
    {
        _clientFactory = clientFactory;
        _topic = topic;
        _subscription = subscription;
        _handler = handler;
        _logger = logger;

        _consumer = clientFactory.CreateConsumerBuilder($"{subscription.ServiceName}-{Environment.MachineName}")
                                 .SetPartitionsAssignedHandler((consumer, partitions) =>
                                 {
                                     foreach (var partition in partitions)
                                         _trackers[partition] = new PartitionOffsetTracker();

                                     // New partitions start fetching; the next poll iteration pauses them again if the consumer is full.
                                     _paused = false;

                                     _logger.Information("{Service} assigned Kafka partitions {Partitions}", _subscription.ServiceName, string.Join(", ", partitions.Select(p => p.Partition.Value)));
                                 })
                                 .SetPartitionsRevokedHandler((consumer, partitions) =>
                                 {
                                     CommitSettled(force: true, only: partitions.Select(p => p.TopicPartition).ToHashSet());

                                     foreach (var partition in partitions)
                                         _trackers.TryRemove(partition.TopicPartition, out _);

                                     _logger.Information("{Service} revoked Kafka partitions {Partitions}", _subscription.ServiceName, string.Join(", ", partitions.Select(p => p.Partition.Value)));
                                 })
                                 .SetPartitionsLostHandler((consumer, partitions) =>
                                 {
                                     foreach (var partition in partitions)
                                         _trackers.TryRemove(partition.TopicPartition, out _);

                                     _logger.Warning("{Service} lost Kafka partitions {Partitions}. Their unsettled records will be delivered again.", _subscription.ServiceName, string.Join(", ", partitions.Select(p => p.Partition.Value)));
                                 })
                                 .Build();
    }

    /// <inheritdoc/>
    public Task Completion => _died.Task;

    public void Start(CancellationToken cancellationToken)
    {
        _consumer.Subscribe(_topic);

        _logger.Information("{Service} consuming from Kafka topic {Topic}. Group: {Group}, Max unsettled: {Prefetch}", _subscription.ServiceName, _topic, KafkaTopicNames.WithPrefix(_clientFactory.Prefix, KafkaTopicNames.ApiGroup), _subscription.Prefetch);

        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);

        _loop = Task.Factory.StartNew(() => PollLoop(linked.Token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                            .ContinueWith(_ => linked.Dispose(), TaskScheduler.Default);
    }

    private void PollLoop(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                CommitSettled(force: false);

                ApplyBackpressure();

                ConsumeResult<string, byte[]> result;

                try
                {
                    result = _consumer.Consume(_pollTimeout);
                }
                catch (ConsumeException ex) when (!ex.Error.IsFatal)
                {
                    _logger.Warning(ex, "{Service} Kafka consume error: {Reason}", _subscription.ServiceName, ex.Error.Reason);
                    continue;
                }

                if (result?.Message == null || result.IsPartitionEOF)
                    continue;

                var tracker = _trackers.GetOrAdd(result.TopicPartition, _ => new PartitionOffsetTracker());

                tracker.Add(result.Offset.Value);

                var delivery = new KafkaDelivery(result, tracker, _clientFactory, _logger);

                try
                {
                    _handler(delivery).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error in {Service} consumer handler. Offset: {Offset}", _subscription.ServiceName, result.TopicPartitionOffset);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping: the consumer stays open until disposed, so pending records can still be settled and committed.
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "{Service} Kafka consumer stopped", _subscription.ServiceName);
            _died.TrySetResult();
        }
    }

    /// <summary>
    /// Pauses fetching while the consumer holds as many unsettled records as it may, and keeps polling so the group
    /// does not evict it.
    /// </summary>
    private void ApplyBackpressure()
    {
        var unsettled = _trackers.Values.Sum(t => t.InFlight);
        var full = unsettled >= Math.Max(1, (int)_subscription.Prefetch);

        if (full == _paused)
            return;

        var assignment = _consumer.Assignment;

        if (assignment.Count == 0)
            return;

        if (full)
            _consumer.Pause(assignment);
        else
            _consumer.Resume(assignment);

        _paused = full;
    }

    private void CommitSettled(bool force, HashSet<TopicPartition> only = null)
    {
        if (!force && DateTime.UtcNow - _lastCommit < _commitInterval)
            return;

        lock (_commitLock)
        {
            _lastCommit = DateTime.UtcNow;

            var offsets = new List<(TopicPartitionOffset Offset, PartitionOffsetTracker Tracker)>();

            foreach (var (partition, tracker) in _trackers)
            {
                if (only != null && !only.Contains(partition))
                    continue;

                if (tracker.GetCommittable() is long next)
                    offsets.Add((new TopicPartitionOffset(partition, new Offset(next)), tracker));
            }

            if (offsets.Count == 0)
                return;

            try
            {
                _consumer.Commit(offsets.Select(o => o.Offset));

                foreach (var (offset, tracker) in offsets)
                    tracker.MarkCommitted(offset.Offset.Value);
            }
            catch (KafkaException ex)
            {
                _logger.Warning(ex, "{Service} could not commit Kafka offsets: {Reason}", _subscription.ServiceName, ex.Error.Reason);
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        await _stop.CancelAsync();

        try
        {
            await _loop;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "{Service} Kafka poll loop ended with an error", _subscription.ServiceName);
        }

        try
        {
            CommitSettled(force: true);
            _consumer.Close();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "{Service} could not close its Kafka consumer cleanly", _subscription.ServiceName);
        }
        finally
        {
            _consumer.Dispose();
            _stop.Dispose();
        }
    }
}

/// <summary>
/// A Kafka record. Ack makes its offset committable; nack with requeue writes it back to its topic first.
/// </summary>
internal sealed class KafkaDelivery : IMessageDelivery
{
    private readonly ConsumeResult<string, byte[]> _result;
    private readonly PartitionOffsetTracker _tracker;
    private readonly KafkaClientFactory _clientFactory;
    private readonly IMilvaLogger _logger;

    public KafkaDelivery(ConsumeResult<string, byte[]> result, PartitionOffsetTracker tracker, KafkaClientFactory clientFactory, IMilvaLogger logger)
    {
        _result = result;
        _tracker = tracker;
        _clientFactory = clientFactory;
        _logger = logger;

        Headers = new Dictionary<string, object>(StringComparer.Ordinal);

        if (result.Message.Headers != null)
        {
            foreach (var header in result.Message.Headers)
                Headers[header.Key] = header.GetValueBytes();
        }

        CorrelationId = MessageHeaderValues.GetString(Headers, KafkaHeaderNames.CorrelationId) ?? MessageHeaderValues.GetString(Headers, KafkaHeaderNames.OccurrenceId);
    }

    public ReadOnlyMemory<byte> Body => _result.Message.Value ?? [];

    public string MessageId => _result.Message.Key;

    public string CorrelationId { get; }

    public IDictionary<string, object> Headers { get; }

    public ulong DeliveryTag => (ulong)_result.Offset.Value;

    public Task AckAsync(CancellationToken cancellationToken)
    {
        _tracker.Complete(_result.Offset.Value);

        return Task.CompletedTask;
    }

    public async Task NackAsync(bool requeue, CancellationToken cancellationToken)
    {
        if (requeue)
        {
            try
            {
                await _clientFactory.Producer.ProduceAsync(_result.Topic, new Message<string, byte[]>
                {
                    Key = _result.Message.Key,
                    Value = _result.Message.Value,
                    Headers = _result.Message.Headers
                }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Left unsettled: the offset is not committed, so the record is delivered again after a restart or rebalance.
                _logger.Warning(ex, "Could not requeue Kafka record {Offset}", _result.TopicPartitionOffset);

                return;
            }
        }
        else
        {
            _logger.Debug("Dropping rejected Kafka record {Offset}", _result.TopicPartitionOffset);
        }

        _tracker.Complete(_result.Offset.Value);
    }
}
