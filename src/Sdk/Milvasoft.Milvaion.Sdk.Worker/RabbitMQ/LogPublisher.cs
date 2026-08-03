using Microsoft.Extensions.Logging;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Helpers;
using Milvasoft.Milvaion.Sdk.Utils;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using RabbitMQ.Client;
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
/// </summary>
public class LogPublisher(WorkerOptions options, ILoggerFactory loggerFactory) : ILogPublisher
{
    private readonly WorkerOptions _options = options;
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<LogPublisher>();
    private IConnection _connection;
    private IChannel _channel;
    private bool _disposed;

    // Batching configuration
    private readonly ConcurrentQueue<WorkerLogMessage> _logBuffer = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly int _batchSize = 50; // Flush when 50 logs accumulated
    private readonly TimeSpan _flushInterval = TimeSpan.FromMilliseconds(500); // Or every 500ms
    private Timer _flushTimer;
    private DateTime _lastFlushTime = DateTime.UtcNow;

    public async Task PublishLogAsync(Guid occurrenceId,
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
            _logBuffer.Enqueue(message);

            // Start flush timer if not already running
            _flushTimer ??= new Timer(async _ =>
            {
                try
                {
                    await FlushLogsAsync(CancellationToken.None);
                }
                catch (Exception timerEx)
                {
                    _logger?.Error(timerEx, "Timer flush failed - logs remain in buffer for next flush");
                }
            }, null, _flushInterval, _flushInterval);

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
    }

    /// <summary>
    /// Manually flushes all buffered logs immediately.
    /// Public method for explicit flush before job completion.
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default) => await FlushLogsAsync(cancellationToken);

    /// <summary>
    /// Flushes buffered logs to RabbitMQ as a single batch message.
    /// </summary>
    private async Task FlushLogsAsync(CancellationToken cancellationToken)
    {
        // Wait for lock instead of skipping (prevent log loss)
        await _flushLock.WaitAsync(cancellationToken);

        try
        {
            if (_logBuffer.IsEmpty)
                return;

            // Dequeue all logs
            var batch = new List<WorkerLogMessage>();
            while (_logBuffer.TryDequeue(out var log) && batch.Count < _batchSize * 2) // Max 100 per flush
            {
                batch.Add(log);
            }

            if (batch.IsNullOrEmpty())
                return;

            try
            {
                await EnsureConnectionAsync(cancellationToken);

                // Publish as single batch message
                var batchMessage = new WorkerLogBatchMessage
                {
                    Logs = batch,
                    BatchTimestamp = DateTime.UtcNow
                };

                var json = JsonSerializer.Serialize(batchMessage);
                var body = Encoding.UTF8.GetBytes(json);

                await _channel.BasicPublishAsync(exchange: string.Empty,
                                                 routingKey: WorkerConstant.Queues.WorkerLogs,
                                                 mandatory: false,
                                                 body: body,
                                                 cancellationToken: cancellationToken);

                _lastFlushTime = DateTime.UtcNow;
                _logger.Debug("Flushed {Count} logs to RabbitMQ as single batch message", batch.Count);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to flush logs batch. Re-queuing {Count} logs for retry.", batch.Count);

                // Re-queue logs on failure to prevent data loss
                foreach (var log in batch)
                {
                    _logBuffer.Enqueue(log);
                }

                throw; // Re-throw to trigger retry mechanism
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    private async Task EnsureConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection == null || !_connection.IsOpen)
        {
            var factory = new ConnectionFactory
            {
                HostName = _options.RabbitMQ.Host,
                Port = _options.RabbitMQ.Port,
                UserName = _options.RabbitMQ.Username,
                Password = _options.RabbitMQ.Password,
                VirtualHost = _options.RabbitMQ.VirtualHost
            };

            _connection = await factory.CreateConnectionAsync(cancellationToken);

            // Publisher confirms enabled: BasicPublishAsync below awaits the broker's ack, per https://www.rabbitmq.com/docs/publishers#data-safety.
            _channel = await _connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), cancellationToken);

            await _channel.QueueDeclareAsync(queue: WorkerConstant.Queues.WorkerLogs,
                                             durable: true,
                                             exclusive: false,
                                             autoDelete: false,
                                             arguments: _options.RabbitMQ.BuildQueueArguments(),
                                             cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// Dispose failed connection to allow reconnection on next attempt.
    /// </summary>
    private async Task DisposeConnectionAsync()
    {
        try
        {
            if (_channel != null)
            {
                await _channel.CloseAsync();
                _channel.Dispose();
                _channel = null;
            }

            if (_connection != null)
            {
                await _connection.CloseAsync();
                _connection.Dispose();
                _connection = null;
            }
        }
        catch
        {
            // Ignore disposal errors
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Registered as both LogPublisher and ILogPublisher singletons pointing at the same instance,
        // so the DI container disposes it twice - guard against re-entering with an already-disposed _flushLock.
        if (_disposed)
            return;

        _disposed = true;

        // Flush remaining logs before disposal
        if (_flushTimer != null)
        {
            await _flushTimer.DisposeAsync();
            _flushTimer = null;
        }

        await FlushLogsAsync(CancellationToken.None);

        await DisposeConnectionAsync();

        _flushLock?.Dispose();

        GC.SuppressFinalize(this);
    }
}
