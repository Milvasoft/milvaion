using Microsoft.Extensions.Logging;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Helpers;
using Milvasoft.Milvaion.Sdk.Utils;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using Milvasoft.Milvaion.Sdk.Worker.Persistence;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Milvasoft.Milvaion.Sdk.Worker.RabbitMQ;

/// <summary>
/// Interface for publishing worker logs.
/// </summary>
public interface ILogPublisher : IAsyncDisposable
{
    /// <summary>
    /// Publishes a log entry to RabbitMQ.
    /// </summary>
    Task PublishLogAsync(Guid occurrenceId,
                         string workerId,
                         OccurrenceLog log,
                         CancellationToken cancellationToken = default);

    /// <summary>
    /// Manually flushes buffered logs to RabbitMQ immediately.
    /// Call this before job completion to ensure all logs are sent.
    /// </summary>
    Task FlushAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Publishes worker logs to RabbitMQ with batching for high-throughput scenarios.
/// Logs are buffered and sent in batches to reduce RabbitMQ overhead.
/// A batch that cannot be published is moved to the local store when one is available;
/// otherwise it is held in memory and retried first, so logs keep their order.
/// </summary>
public class LogPublisher(WorkerOptions options, ILoggerFactory loggerFactory, ILocalStateStore localStore = null) : ILogPublisher
{
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<LogPublisher>();
    private readonly ResilientPublisherChannel _channel = new(options, loggerFactory.CreateMilvaLogger<LogPublisher>(), WorkerConstant.Queues.WorkerLogs);
    private readonly ILocalStateStore _localStore = localStore;
    private bool _disposed;

    // Batching configuration
    private readonly ConcurrentQueue<WorkerLogMessage> _logBuffer = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly int _batchSize = 50; // Flush when 50 logs accumulated
    private readonly TimeSpan _flushInterval = TimeSpan.FromMilliseconds(500); // Or every 500ms
    private readonly Lock _timerLock = new();
    private Timer _flushTimer;
    private const int _maxBufferedLogs = 10_000;
    private long _droppedLogs;

    // The batch whose publish failed and could not be persisted. Accessed under _flushLock.
    private List<WorkerLogMessage> _unpublishedBatch;

    public Task PublishLogAsync(Guid occurrenceId,
                                string workerId,
                                OccurrenceLog log,
                                CancellationToken cancellationToken = default)
    {
        try
        {
            var message = new WorkerLogMessage
            {
                OccurrenceId = occurrenceId,
                WorkerId = workerId,
                Log = log,
                MessageTimestamp = DateTime.UtcNow
            };

            // Add to buffer
            EnqueueBounded(message);

            // Start flush timer if not already running
            EnsureFlushTimer();

            // Flush immediately if batch size reached
            if (_logBuffer.Count >= _batchSize)
            {
                // Fire-and-forget for non-blocking (timer will retry if this fails)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await FlushLogsAsync(cancellationToken);
                    }
                    catch (Exception flushEx)
                    {
                        _logger?.Error(flushEx, "Batch-size flush failed - timer will retry");
                    }
                }, cancellationToken);
            }

            _logger.Debug("Log buffered for OccurrenceId: {OccurrenceId} (Buffer: {Count}/{BatchSize})", occurrenceId, _logBuffer.Count, _batchSize);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Failed to buffer log for OccurrenceId: {OccurrenceId}", occurrenceId);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Manually flushes all buffered logs immediately.
    /// Public method for explicit flush before job completion.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        // One flush sends at most one batch. Bounded by what is buffered now, so concurrent producers cannot keep this looping.
        var remainingFlushes = _logBuffer.Count / (_batchSize * 2) + 1;

        while (remainingFlushes-- > 0 && await FlushLogsAsync(cancellationToken))
        {
        }
    }

    /// <summary>
    /// Flushes one batch of buffered logs to RabbitMQ as a single message.
    /// </summary>
    /// <returns><see langword="true"/> if a batch was published; <see langword="false"/> if there was nothing to send or it went to the local store.</returns>
    private async Task<bool> FlushLogsAsync(CancellationToken cancellationToken)
    {
        // Wait for lock instead of skipping (prevent log loss)
        await _flushLock.WaitAsync(cancellationToken);

        try
        {
            var batch = _unpublishedBatch ?? DequeueBatch();

            if (batch.IsNullOrEmpty())
                return false;

            try
            {
                // Publish as single batch message
                var batchMessage = new WorkerLogBatchMessage
                {
                    Logs = batch,
                    BatchTimestamp = DateTime.UtcNow
                };

                var json = JsonSerializer.Serialize(batchMessage);
                var body = Encoding.UTF8.GetBytes(json);

                await _channel.PublishAsync(WorkerConstant.Queues.WorkerLogs, body, persistent: true, cancellationToken);

                _unpublishedBatch = null;

                _logger.Debug("Flushed {Count} logs to RabbitMQ as single batch message", batch.Count);

                return true;
            }
            catch (Exception ex)
            {
                if (await TryStoreLocallyAsync(batch))
                {
                    _logger?.Warning(ex, "Failed to flush logs batch. Stored {Count} logs locally for later sync.", batch.Count);
                    _unpublishedBatch = null;

                    return false;
                }

                _logger?.Error(ex, "Failed to flush logs batch. Keeping {Count} logs for retry.", batch.Count);

                _unpublishedBatch = batch;

                throw; // Re-throw to trigger retry mechanism
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    private List<WorkerLogMessage> DequeueBatch()
    {
        var batch = new List<WorkerLogMessage>();

        while (batch.Count < _batchSize * 2 && _logBuffer.TryDequeue(out var log)) // Max 100 per flush
            batch.Add(log);

        return batch;
    }

    private async Task<bool> TryStoreLocallyAsync(List<WorkerLogMessage> batch)
    {
        if (_localStore == null)
            return false;

        var stored = 0;

        try
        {
            foreach (var message in batch)
            {
                await _localStore.StoreLogAsync(message.OccurrenceId, message.WorkerId, message.Log, CancellationToken.None);
                stored++;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Failed to store logs batch locally");

            // Already stored entries are not retried, they would be delivered twice.
            batch.RemoveRange(0, stored);

            return false;
        }
    }

    private void EnqueueBounded(WorkerLogMessage message)
    {
        if (_logBuffer.Count >= _maxBufferedLogs && _logBuffer.TryDequeue(out _))
        {
            var dropped = Interlocked.Increment(ref _droppedLogs);

            if (dropped == 1 || dropped % 1000 == 0)
                _logger?.Warning("Log buffer is full ({Capacity}). {Dropped} oldest log(s) dropped so far.", _maxBufferedLogs, dropped);
        }

        _logBuffer.Enqueue(message);
    }

    private void EnsureFlushTimer()
    {
        if (_flushTimer != null)
            return;

        lock (_timerLock)
        {
            if (_flushTimer != null || _disposed)
                return;

            _flushTimer = new Timer(async _ =>
            {
                try
                {
                    await FlushLogsAsync(CancellationToken.None);
                }
                catch (Exception timerEx)
                {
                    _logger?.Debug(timerEx, "Timer flush failed - logs remain in buffer for next flush");
                }
            }, null, _flushInterval, _flushInterval);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Registered as both LogPublisher and ILogPublisher singletons pointing at the same instance,
        // so the DI container disposes it twice - guard against re-entering with an already-disposed _flushLock.
        Timer timer;

        lock (_timerLock)
        {
            if (_disposed)
                return;

            _disposed = true;

            timer = _flushTimer;
            _flushTimer = null;
        }

        // Flush remaining logs before disposal
        if (timer != null)
            await timer.DisposeAsync();

        try
        {
            await FlushAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger?.Warning(ex, "Failed to flush remaining logs during shutdown");
        }

        await _channel.DisposeAsync();

        _flushLock?.Dispose();

        GC.SuppressFinalize(this);
    }
}
