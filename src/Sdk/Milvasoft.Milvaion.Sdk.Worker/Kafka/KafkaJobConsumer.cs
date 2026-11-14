using Confluent.Kafka;
using Microsoft.Extensions.Options;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Models;
using Milvasoft.Milvaion.Sdk.Utils.Kafka;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using Milvasoft.Milvaion.Sdk.Worker.Persistence;
using Milvasoft.Milvaion.Sdk.Worker.RabbitMQ;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Milvasoft.Milvaion.Sdk.Worker.Kafka;

/// <summary>
/// A job record read from the worker's job topic or retry topic.
/// </summary>
internal sealed class KafkaJobDelivery : JobDelivery
{
    private readonly Dictionary<string, object> _headers = new(StringComparer.Ordinal);

    public KafkaJobDelivery(ConsumeResult<string, byte[]> record, PartitionOffsetTracker tracker)
    {
        Record = record;
        Tracker = tracker;

        if (record.Message.Headers != null)
        {
            foreach (var header in record.Message.Headers)
                _headers[header.Key] = header.GetValueBytes();
        }

        CorrelationId = MessageHeaderValues.GetGuid(_headers, KafkaHeaderNames.OccurrenceId)
                        ?? MessageHeaderValues.GetGuid(_headers, KafkaHeaderNames.CorrelationId)
                        ?? Guid.Empty;
        RetryCount = MessageHeaderValues.GetInt(_headers, KafkaHeaderNames.RetryCount) ?? 0;
        JobType = MessageHeaderValues.GetString(_headers, KafkaHeaderNames.JobType);
        JobId = Guid.TryParse(record.Message.Key, out var jobId) ? jobId : Guid.Empty;

        var notBefore = MessageHeaderValues.GetString(_headers, KafkaHeaderNames.NotBefore);

        NotBefore = long.TryParse(notBefore, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixMs)
            ? DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime
            : DateTime.MinValue;
    }

    public ConsumeResult<string, byte[]> Record { get; }

    public PartitionOffsetTracker Tracker { get; }

    public TopicPartition Partition => Record.TopicPartition;

    public long Offset => Record.Offset.Value;

    public string JobType { get; }

    public Guid JobId { get; }

    /// <summary>When the record may run. <see cref="DateTime.MinValue"/> for records without <c>x-not-before</c>.</summary>
    public DateTime NotBefore { get; }

    public override ReadOnlyMemory<byte> Body => Record.Message.Value ?? [];

    public override Guid CorrelationId { get; }

    public override int RetryCount { get; }

    /// <summary>Kafka keeps no delivery count; a record read again after a crash is not flagged.</summary>
    public override bool Redelivered => false;
}

/// <summary>
/// Consumes the worker's Kafka job topic and retry topic. One consumer per worker instance, in the worker's group.
/// See docs/githubdocs/KAFKA-TRANSPORT.md for the design.
/// </summary>
/// <remarks>
/// <para>The poll loop runs on its own thread and never blocks: records wait in a ready list for an instance slot (and a
/// slot of their job type), and retry records wait in a held list until their <c>x-not-before</c>. Partitions are paused
/// while they hold <see cref="KafkaSettings.MaxInFlightPerPartition"/> unfinished records, and all of them while the ready
/// list is full, so the consumer keeps polling and is never evicted from the group.</para>
/// <para>Each partition has a <see cref="PartitionOffsetTracker"/>; only the end of its contiguous finished range is
/// committed, so a record is never committed before its job's outcome has been reported.</para>
/// </remarks>
public sealed class KafkaJobConsumer : JobConsumer
{
    private static readonly TimeSpan _pollTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan _commitInterval = TimeSpan.FromMilliseconds(500);

    private readonly KafkaWorkerClients _clients;
    private readonly ConcurrentDictionary<TopicPartition, PartitionOffsetTracker> _trackers = new();
    private readonly ConcurrentDictionary<Guid, (Guid JobId, DateTime Until)> _retryHeartbeats = new();
    private readonly Dictionary<string, SemaphoreSlim> _jobTypeSlots;
    private readonly Lock _commitLock = new();

    // Owned by the poll thread.
    private readonly List<KafkaJobDelivery> _ready = [];
    private readonly List<KafkaJobDelivery> _held = [];
    private readonly HashSet<TopicPartition> _paused = [];

    private IConsumer<string, byte[]> _consumer;
    private SemaphoreSlim _slots;
    private int _running;
    private int _maxInFlightPerPartition;
    private DateTime _lastCommit = DateTime.MinValue;
    private DateTime _lastWaitingHeartbeat = DateTime.UtcNow;

    /// <summary>
    /// Initializes a new instance of the <see cref="KafkaJobConsumer"/> class.
    /// </summary>
    public KafkaJobConsumer(IServiceProvider serviceProvider,
                            IOptions<WorkerOptions> options,
                            IOptions<JobConsumerOptions> jobConsumerOptions,
                            IMilvaLogger logger,
                            KafkaWorkerClients clients,
                            OutboxService outboxService = null) : base(serviceProvider, options, jobConsumerOptions, logger, outboxService)
    {
        _clients = clients;
        _jobTypeSlots = _jobConfigs.ToDictionary(c => c.Key, c => new SemaphoreSlim(Math.Max(1, c.Value.MaxParallelJobs)), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Jobs running right now.
    /// </summary>
    public int RunningJobs => Volatile.Read(ref _running);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var maxParallelJobs = _options.MaxParallelJobs;

        if (maxParallelJobs <= 0)
            throw new InvalidOperationException($"MaxParallelJobs must be greater than zero but was {maxParallelJobs}. Set Worker:MaxParallelJobs.");

        _slots = new SemaphoreSlim(maxParallelJobs, maxParallelJobs);
        _maxInFlightPerPartition = _clients.Settings.MaxInFlightPerPartition > 0 ? _clients.Settings.MaxInFlightPerPartition : maxParallelJobs;

        await _clients.EnsureWorkerTopicsAsync(stoppingToken);

        _consumer = new ConsumerBuilder<string, byte[]>(KafkaWorkerClients.BuildConsumerConfig(_clients.Settings, _clients.Group, _options.InstanceId))
            .SetErrorHandler((_, error) => _logger?.Warning("Kafka consumer error: {Reason} (Code: {Code}, Fatal: {Fatal})", error.Reason, error.Code, error.IsFatal))
            .SetPartitionsAssignedHandler((consumer, partitions) => OnAssigned(partitions))
            .SetPartitionsRevokedHandler((consumer, partitions) => OnRevoked([.. partitions.Select(p => p.TopicPartition)], commit: true))
            .SetPartitionsLostHandler((consumer, partitions) => OnRevoked([.. partitions.Select(p => p.TopicPartition)], commit: false))
            .Build();

        _consumer.Subscribe([_clients.JobsTopic, _clients.RetryTopic]);

        _logger?.Information("Worker {InstanceId} consuming Kafka topics {JobsTopic} and {RetryTopic} in group {Group}. MaxParallelJobs: {MaxParallel}, MaxInFlightPerPartition: {PerPartition}",
                             _options.InstanceId, _clients.JobsTopic, _clients.RetryTopic, _clients.Group, maxParallelJobs, _maxInFlightPerPartition);

        try
        {
            await Task.Factory.StartNew(() => PollLoop(stoppingToken), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        finally
        {
            _logger?.Information("Worker {InstanceId} is shutting down, waiting for {Count} running jobs...", _options.InstanceId, RunningJobs);

            await WaitForRunningJobsAsync();

            try
            {
                CommitSettled(force: true);
                _consumer.Close();
            }
            catch (Exception ex)
            {
                _logger?.Warning(ex, "Worker {InstanceId} could not close its Kafka consumer cleanly", _options.InstanceId);
            }
            finally
            {
                _consumer.Dispose();
            }
        }
    }

    private void PollLoop(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            CommitSettled(force: false);
            ReleaseDueRecords();
            Dispatch(stoppingToken);
            UpdatePauses();
            SendWaitingHeartbeats();

            ConsumeResult<string, byte[]> record;

            try
            {
                record = _consumer.Consume(_pollTimeout);
            }
            catch (ConsumeException ex) when (!ex.Error.IsFatal)
            {
                _logger?.Warning(ex, "Kafka consume error: {Reason}", ex.Error.Reason);
                continue;
            }

            if (record?.Message == null || record.IsPartitionEOF)
                continue;

            var tracker = _trackers.GetOrAdd(record.TopicPartition, _ => new PartitionOffsetTracker());

            tracker.Add(record.Offset.Value);

            var delivery = new KafkaJobDelivery(record, tracker);

            if (delivery.NotBefore > DateTime.UtcNow)
                _held.Add(delivery);
            else
                _ready.Add(delivery);

            Dispatch(stoppingToken);
        }
    }

    /// <summary>
    /// Starts every ready record that has a free instance slot and a free slot of its job type, in arrival order.
    /// </summary>
    private void Dispatch(CancellationToken stoppingToken)
    {
        for (var i = 0; i < _ready.Count;)
        {
            if (!_slots.Wait(0))
                return;

            var delivery = _ready[i];
            var typeSlot = delivery.JobType != null ? _jobTypeSlots.GetValueOrDefault(delivery.JobType) : null;

            if (typeSlot != null && !typeSlot.Wait(0))
            {
                // This job type is at its limit; a later record of another type may still run.
                _slots.Release();
                i++;
                continue;
            }

            _ready.RemoveAt(i);

            // Picked up: from here on the attempt itself reports that the occurrence is alive.
            _retryHeartbeats.TryRemove(delivery.CorrelationId, out _);

            Interlocked.Increment(ref _running);

            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessMessageAsync(delivery, stoppingToken);
                }
                finally
                {
                    typeSlot?.Release();
                    _slots.Release();
                    Interlocked.Decrement(ref _running);
                }
            }, CancellationToken.None);
        }
    }

    private void ReleaseDueRecords()
    {
        if (_held.Count == 0)
            return;

        var now = DateTime.UtcNow;

        foreach (var delivery in _held.Where(d => d.NotBefore <= now).OrderBy(d => d.NotBefore).ToList())
        {
            _held.Remove(delivery);
            _ready.Add(delivery);
        }
    }

    /// <summary>
    /// Pauses partitions that hold as many unfinished records as allowed, and every partition while the ready list is full.
    /// </summary>
    private void UpdatePauses()
    {
        var assignment = _consumer.Assignment;

        if (assignment.Count == 0)
            return;

        var pauseAll = _ready.Count >= _options.MaxParallelJobs;
        var toPause = new List<TopicPartition>();
        var toResume = new List<TopicPartition>();

        foreach (var partition in assignment)
        {
            var full = pauseAll || (_trackers.TryGetValue(partition, out var tracker) && tracker.InFlight >= _maxInFlightPerPartition);

            if (full && _paused.Add(partition))
                toPause.Add(partition);
            else if (!full && _paused.Remove(partition))
                toResume.Add(partition);
        }

        if (toPause.Count > 0)
            _consumer.Pause(toPause);

        if (toResume.Count > 0)
            _consumer.Resume(toResume);
    }

    /// <summary>
    /// Keeps the occurrences of failed jobs that wait for their retry from being taken for zombies: the occurrence is
    /// still Running during the backoff, but no attempt is running to send its heartbeat.
    /// </summary>
    private void SendWaitingHeartbeats()
    {
        var interval = _options.Heartbeat?.JobHeartbeatIntervalSeconds ?? 60;

        if (_outboxService == null || interval <= 0 || DateTime.UtcNow - _lastWaitingHeartbeat < TimeSpan.FromSeconds(interval))
            return;

        _lastWaitingHeartbeat = DateTime.UtcNow;

        var waiting = new Dictionary<Guid, Guid>();

        foreach (var delivery in _held)
            waiting[delivery.CorrelationId] = delivery.JobId;

        foreach (var (correlationId, entry) in _retryHeartbeats)
        {
            if (entry.Until <= DateTime.UtcNow)
                _retryHeartbeats.TryRemove(correlationId, out _);
            else
                waiting[correlationId] = entry.JobId;
        }

        foreach (var (correlationId, jobId) in waiting)
        {
            if (correlationId == Guid.Empty)
                continue;

            _ = Task.Run(async () =>
            {
                try
                {
                    await _outboxService.PublishJobHeartbeatAsync(correlationId, jobId, _options.WorkerId, _options.InstanceId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Heartbeat for waiting retry {CorrelationId} failed (non-critical)", correlationId);
                }
            });
        }
    }

    private void OnAssigned(List<TopicPartition> partitions)
    {
        foreach (var partition in partitions)
            _trackers[partition] = new PartitionOffsetTracker();

        _logger?.Information("Worker {InstanceId} assigned Kafka partitions {Partitions}", _options.InstanceId, string.Join(", ", partitions.Select(p => $"{p.Topic}[{p.Partition.Value}]")));
    }

    private void OnRevoked(List<TopicPartition> partitions, bool commit)
    {
        var revoked = partitions.ToHashSet();

        if (commit)
            CommitSettled(force: true, only: revoked);

        foreach (var partition in partitions)
        {
            _trackers.TryRemove(partition, out _);
            _paused.Remove(partition);
        }

        // Records not started yet go to the partition's new owner.
        _ready.RemoveAll(d => revoked.Contains(d.Partition));
        _held.RemoveAll(d => revoked.Contains(d.Partition));

        _logger?.Information("Worker {InstanceId} {Action} Kafka partitions {Partitions}", _options.InstanceId, commit ? "revoked" : "lost", string.Join(", ", partitions.Select(p => $"{p.Topic}[{p.Partition.Value}]")));
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
                _logger?.Warning(ex, "Worker {InstanceId} could not commit Kafka offsets: {Reason}", _options.InstanceId, ex.Error.Reason);
            }
        }
    }

    private async Task WaitForRunningJobsAsync()
    {
        var deadline = DateTime.UtcNow + _shutdownTimeout;

        while (RunningJobs > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        if (RunningJobs > 0)
            _logger?.Warning("Worker {InstanceId} gave up waiting for {Count} running job(s) after {Timeout}s. Their records will be delivered again.", _options.InstanceId, RunningJobs, _shutdownTimeout.TotalSeconds);
        else
            _logger?.Information("Worker {InstanceId} all jobs completed", _options.InstanceId);
    }

    #region Settlement

    private protected override Task AckAsync(JobDelivery delivery, IMilvaLogger logger, Guid jobId)
    {
        var record = (KafkaJobDelivery)delivery;

        record.Tracker.Complete(record.Offset);

        logger?.Debug("Job {JobId} finished, Kafka record {Offset} can be committed", jobId, record.Record.TopicPartitionOffset);

        return Task.CompletedTask;
    }

    private protected override async Task NackAsync(JobDelivery delivery, IMilvaLogger logger, Guid correlationId, bool requeue = false)
    {
        var record = (KafkaJobDelivery)delivery;

        try
        {
            if (requeue)
            {
                // The Kafka form of a nack with requeue: the record goes back to the end of its own topic.
                await _clients.Producer.ProduceAsync(record.Record.Topic, new Message<string, byte[]>
                {
                    Key = record.Record.Message.Key,
                    Value = record.Record.Message.Value,
                    Headers = record.Record.Message.Headers
                }, CancellationToken.None);

                logger?.Debug("Kafka record {Offset} written back to {Topic} (CorrelationId: {CorrelationId})", record.Record.TopicPartitionOffset, record.Record.Topic, correlationId);
            }
            else
            {
                // The Kafka form of a dead-lettering nack: the raw record goes to the dead letter topic.
                var headers = record.Record.Message.Headers ?? [];

                headers.Add(KafkaHeaderNames.OriginalTopic, Encoding.UTF8.GetBytes(record.Record.Topic));
                headers.Add(KafkaHeaderNames.DlqReason, Encoding.UTF8.GetBytes("rejected"));

                await _clients.Producer.ProduceAsync(_clients.DeadLetterTopic, new Message<string, byte[]>
                {
                    Key = correlationId == Guid.Empty ? record.Record.Message.Key : correlationId.ToString(),
                    Value = record.Record.Message.Value,
                    Headers = headers
                }, CancellationToken.None);

                logger?.Debug("Kafka record {Offset} sent to the dead letter topic (CorrelationId: {CorrelationId})", record.Record.TopicPartitionOffset, correlationId);
            }

            record.Tracker.Complete(record.Offset);
        }
        catch (Exception ex)
        {
            // Left unfinished: the offset is not committed, so the record is delivered again after a restart or rebalance.
            logger?.Error(ex, "Could not settle Kafka record {Offset} (Requeue: {Requeue}). It will be delivered again.", record.Record.TopicPartitionOffset, requeue);
        }
    }

    private protected override async Task RetryAsync(JobDelivery delivery, ScheduledJob job, Guid correlationId, int newRetryCount, int maxRetries, int delaySeconds, CancellationToken cancellationToken)
    {
        var record = (KafkaJobDelivery)delivery;
        var due = DateTime.UtcNow.AddSeconds(delaySeconds);
        var occurrence = Encoding.UTF8.GetBytes(correlationId.ToString());

        var headers = new Headers
        {
            { KafkaHeaderNames.OccurrenceId, occurrence },
            { KafkaHeaderNames.CorrelationId, occurrence },
            { KafkaHeaderNames.JobType, Encoding.UTF8.GetBytes(job.JobNameInWorker ?? record.JobType ?? string.Empty) },
            { KafkaHeaderNames.WorkerId, Encoding.UTF8.GetBytes(_options.WorkerId ?? string.Empty) },
            { KafkaHeaderNames.RetryCount, Encoding.UTF8.GetBytes(newRetryCount.ToString(CultureInfo.InvariantCulture)) },
            { KafkaHeaderNames.MaxRetries, Encoding.UTF8.GetBytes(maxRetries.ToString(CultureInfo.InvariantCulture)) },
            { KafkaHeaderNames.NotBefore, Encoding.UTF8.GetBytes(new DateTimeOffset(due).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)) }
        };

        // The job body goes on unchanged; the backoff is carried by x-not-before, so no slot is held while waiting.
        await _clients.Producer.ProduceAsync(_clients.RetryTopic, new Message<string, byte[]>
        {
            Key = record.Record.Message.Key ?? job.Id.ToString(),
            Value = record.Record.Message.Value,
            Headers = headers
        }, CancellationToken.None);

        _retryHeartbeats[correlationId] = (job.Id, due);

        _logger?.Debug("Job {JobId} scheduled for retry {RetryCount} at {Due:O} on {Topic}", job.Id, newRetryCount, due, _clients.RetryTopic);
    }

    private protected override async Task PublishToDeadLetterAsync(DlqJobMessage message, Guid correlationId, int retryCount, int maxRetries, CancellationToken cancellationToken)
    {
        var occurrence = Encoding.UTF8.GetBytes(correlationId.ToString());

        await _clients.Producer.ProduceAsync(_clients.DeadLetterTopic, new Message<string, byte[]>
        {
            Key = correlationId.ToString(),
            Value = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)),
            Headers = new Headers
            {
                { KafkaHeaderNames.OccurrenceId, occurrence },
                { KafkaHeaderNames.CorrelationId, occurrence },
                { KafkaHeaderNames.RetryCount, Encoding.UTF8.GetBytes(retryCount.ToString(CultureInfo.InvariantCulture)) },
                { KafkaHeaderNames.MaxRetries, Encoding.UTF8.GetBytes(maxRetries.ToString(CultureInfo.InvariantCulture)) }
            }
        }, CancellationToken.None);

        _retryHeartbeats.TryRemove(correlationId, out _);

        _logger?.Debug("Job {JobId} published to the Kafka dead letter topic with retry count {RetryCount}/{MaxRetries}", message.Id, retryCount, maxRetries);
    }

    #endregion

    /// <inheritdoc/>
    public override void Dispose()
    {
        _slots?.Dispose();

        foreach (var slot in _jobTypeSlots.Values)
            slot.Dispose();

        base.Dispose();
    }
}
