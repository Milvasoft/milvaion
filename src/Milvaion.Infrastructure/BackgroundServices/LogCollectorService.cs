using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Dtos.AlertingDtos;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.Constants;
using Milvaion.Infrastructure.BackgroundServices.Base;
using Milvaion.Infrastructure.Extensions;
using Milvaion.Infrastructure.Persistence.Context;
using Milvaion.Infrastructure.Services.RabbitMQ;
using Milvaion.Infrastructure.Telemetry;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Helpers;
using Milvasoft.Milvaion.Sdk.Utils;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Milvaion.Infrastructure.BackgroundServices;

/// <summary>
/// Consumes worker logs from RabbitMQ and appends them to JobOccurrence.Logs.
/// </summary>
public class LogCollectorService(IServiceProvider serviceProvider,
                                 RabbitMQConnectionFactory rabbitMQFactory,
                                 IAlertNotifier alertNotifier,
                                 IOptions<LogCollectorOptions> logCollectorOptions,
                                 IOptions<RabbitMQOptions> rabbitMQOptions,
                                 ILoggerFactory loggerFactory,
                                 BackgroundServiceMetrics metrics,
                                 IMemoryStatsRegistry memoryStatsRegistry = null) : MemoryTrackedBackgroundService(loggerFactory, logCollectorOptions.Value, memoryStatsRegistry)
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly RabbitMQConnectionFactory _rabbitMQFactory = rabbitMQFactory;
    private readonly IAlertNotifier _alertNotifier = alertNotifier;
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<LogCollectorService>();
    private readonly LogCollectorOptions _options = logCollectorOptions.Value;
    private readonly RabbitMQOptions _rabbitMQOptions = rabbitMQOptions.Value;
    private readonly BackgroundServiceMetrics _metrics = metrics;
    private IChannel _channel;

    // Channel thread-safety: IChannel is NOT thread-safe, serialize ACK/NACK operations
    private readonly SemaphoreSlim _channelLock = new(1, 1);

    // Batch processing
    private readonly ConcurrentQueue<WorkerLogMessage> _logBatch = new();
    private readonly SemaphoreSlim _batchLock = new(1, 1);
    private const int _maxQueueSize = 100000; // Prevent unbounded memory growth

    // Pending logs - waiting for occurrence to be created (FK constraint race condition)
    private readonly ConcurrentQueue<(WorkerLogMessage Log, int RetryCount, DateTime AddedAt)> _pendingLogs = new();
    private const int _maxPendingRetries = 20; // ~10 seconds with 500ms interval
    private static readonly TimeSpan _pendingMaxAge = TimeSpan.FromMinutes(2);

    /// <inheritdoc/>
    protected override string ServiceName => "LogCollector";

    /// <inheritdoc />
    protected override async Task ExecuteWithMemoryTrackingAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.Warning("Log collection is disabled. Skipping startup.");

            return;
        }

        _logger.Information("Log collection starting...");

        // Start batch processor task
        var batchTask = Task.Run(async () =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(_options.BatchIntervalMs, stoppingToken);

                await ProcessBatchAsync(stoppingToken);

                // Retry pending logs (waiting for occurrence to be created)
                await RetryPendingLogsAsync(stoppingToken);

                TrackMemoryAfterIteration();
            }
        }, stoppingToken);

        var retryCount = 0;
        const int maxRetries = 10;
        const int retryDelaySeconds = 5;

        while (!stoppingToken.IsCancellationRequested && retryCount < maxRetries)
        {
            try
            {
                await ConnectAndConsumeAsync(stoppingToken);

                // If we reach here, connection was successful
                retryCount = 0;
            }
            catch (OperationCanceledException)
            {
                _logger.Information("Log collection is shutting down");

                break;
            }
            catch (Exception ex)
            {
                retryCount++;

                _logger.Error(ex, "LogCollectorService connection failed (attempt {Retry}/{MaxRetries})", retryCount, maxRetries);

                if (retryCount >= maxRetries)
                {
                    _logger.Fatal("LogCollectorService failed to connect after {MaxRetries} attempts. Service will be disabled until application restart.", maxRetries);

                    _alertNotifier.SendFireAndForget(AlertType.ServiceDegraded, new AlertPayload
                    {
                        Title = "LogCollector Service Stopped",
                        Message = $"LogCollectorService failed to connect to RabbitMQ after {maxRetries} attempts. Service is disabled until application restart.",
                        Severity = AlertSeverity.Critical,
                        Source = nameof(LogCollectorService),
                        ThreadKey = "service-degraded-logcollector"
                    });

                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds * retryCount), stoppingToken);
            }
        }
    }

    private async Task ConnectAndConsumeAsync(CancellationToken stoppingToken)
    {
        // Get channel from shared connection factory
        _channel = await _rabbitMQFactory.CreateChannelAsync(stoppingToken);

        // Declare queue (idempotent)
        await _channel.QueueDeclareAsync(queue: WorkerConstant.Queues.WorkerLogs,
                                         durable: true,
                                         exclusive: false,
                                         autoDelete: false,
                                         arguments: _rabbitMQOptions.BuildQueueArguments(),
                                         cancellationToken: stoppingToken);

        // Set prefetch count
        await _channel.BasicQosAsync(0, 10, false, stoppingToken);

        _logger.Information("LogCollector connected to RabbitMQ (shared connection). Queue: {Queue}, ChannelNumber: {ChannelNumber}", WorkerConstant.Queues.WorkerLogs, _channel.ChannelNumber);

        // Setup consumer
        var consumer = new AsyncEventingBasicConsumer(_channel);

        consumer.ReceivedAsync += async (model, ea) =>
        {
            try
            {
                await ProcessLogMessageAsync(ea, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.Fatal(ex, "Unhandled exception in log consumer");
            }
        };

        await _channel.BasicConsumeAsync(queue: WorkerConstant.Queues.WorkerLogs,
                                         autoAck: false,
                                         consumer: consumer,
                                         cancellationToken: stoppingToken);

        _logger.Information("LogCollectorService is now consuming messages...");

        // Keep running until cancellation
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task ProcessLogMessageAsync(BasicDeliverEventArgs ea, CancellationToken cancellationToken)
    {
        try
        {
            // Try to parse as batch message first (new format)
            var batchMessage = JsonSerializer.Deserialize<WorkerLogBatchMessage>(ea.Body.Span, ConstantJsonOptions.PropNameCaseInsensitive);

            if (batchMessage != null && batchMessage.Count > 0)
            {
                // Batch message - add all logs
                foreach (var log in batchMessage.Logs)
                {
                    _logBatch.Enqueue(log);
                }

                _logger.Debug("Received batch message with {Count} logs", batchMessage.Count);

                // Trigger immediate batch if queue is full
                if (_logBatch.Count >= _options.BatchSize)
                {
                    await ProcessBatchAsync(cancellationToken);
                }

                // ACK the message (safe operation)
                await _channel.SafeAckAsync(ea.DeliveryTag, _channelLock, _logger, cancellationToken);
                return;
            }

            // Fallback: Try single message format (backward compatibility)
            var singleMessage = JsonSerializer.Deserialize<WorkerLogMessage>(ea.Body.Span, ConstantJsonOptions.PropNameCaseInsensitive);

            if (singleMessage == null)
            {
                _logger.Debug("Failed to deserialize log message");
                await _channel.SafeNackAsync(ea.DeliveryTag, _channelLock, _logger, cancellationToken);
                return;
            }

            // Check queue size limit before enqueuing (backpressure)
            if (_logBatch.Count >= _maxQueueSize)
            {
                _logger.Warning("Log batch queue is full ({Count} messages). Dropping message to prevent OOM.", _logBatch.Count);
                await _channel.SafeAckAsync(ea.DeliveryTag, _channelLock, _logger, cancellationToken);
                return;
            }

            // Add to batch queue (NO DB operation here!)
            _logBatch.Enqueue(singleMessage);

            // Trigger immediate batch if queue is full
            if (_logBatch.Count >= _options.BatchSize)
            {
                await ProcessBatchAsync(cancellationToken);
            }

            // ACK the message (safe operation)
            await _channel.SafeAckAsync(ea.DeliveryTag, _channelLock, _logger, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to process log message");
            await _channel.SafeNackAsync(ea.DeliveryTag, _channelLock, _logger, cancellationToken);
        }
    }

    /// <summary>
    /// Process batch of logs - single DB transaction for all logs.
    /// Uses optimistic concurrency control to prevent lost updates when multiple instances run concurrently.
    /// </summary>
    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        // If batch processing is already in progress, skip
        if (!await _batchLock.WaitAsync(0, cancellationToken))
            return;

        var sw = Stopwatch.StartNew();

        try
        {
            if (_logBatch.IsEmpty)
                return;

            var batch = new List<WorkerLogMessage>();

            // Dequeue logs with max limit to prevent OOM from processing too many at once
            var maxBatchSize = Math.Min(_options.BatchSize * 10, 10000); // Max 10x batch size or 10k messages
            var dequeueCount = 0;

            while (_logBatch.TryDequeue(out var message) && dequeueCount < maxBatchSize)
            {
                batch.Add(message);
                dequeueCount++;
            }

            if (batch.Count == 0)
                return;

            _metrics.SetLogBatchSize(batch.Count);

            // Retry logic for optimistic concurrency conflicts
            const int maxRetries = 3;
            var retryCount = 0;
            var retryDelay = TimeSpan.FromMilliseconds(50); // Start with 50ms

            while (retryCount < maxRetries)
            {
                try
                {
                    await using var scope = _serviceProvider.CreateAsyncScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

                    // Group by OccurrenceId
                    var logsByOccurrence = batch.GroupBy(m => m.OccurrenceId).ToList();

                    var logsToInsert = batch.Where(l => l?.Log != null).Select(l => new JobOccurrenceLog
                    {
                        Id = Guid.CreateVersion7(),
                        OccurrenceId = l.OccurrenceId,
                        Level = l.Log.Level,
                        Category = l.Log.Category,
                        ExceptionType = l.Log.ExceptionType,
                        Message = l.Log.Message,
                        Data = l.Log.Data,
                        Timestamp = l.Log.Timestamp,
                    });

                    if (!logsToInsert.IsNullOrEmpty())
                        await dbContext.BulkInsertAsync(logsToInsert, cancellationToken: cancellationToken);

                    #region Send Socket Events

                    // Trigger SignalR events after DB update
                    var eventPublisher = scope.ServiceProvider.GetService<IJobOccurrenceEventPublisher>();

                    if (eventPublisher != null)
                    {
                        //  Collect events first, then publish in batch
                        var publishTasks = new List<Task>(batch.Count);

                        foreach (var logToInsert in batch)
                            publishTasks.Add(eventPublisher.PublishLogAddedAsync(logToInsert.OccurrenceId, logToInsert.Log, cancellationToken));

                        // Wait for all events to complete (with timeout)
                        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                        var completedTask = await Task.WhenAny(Task.WhenAll(publishTasks), timeoutTask);

                        if (completedTask == timeoutTask)
                            _logger.Warning("SignalR event publishing timed out after 5 seconds for {Count} events", publishTasks.Count);
                    }

                    #endregion

                    // Record metrics
                    _metrics.RecordLogsCollected(batch.Count);
                    _metrics.RecordLogBatchDuration(sw.Elapsed.TotalMilliseconds, batch.Count);

                    _logger.Debug("Processed {Count} logs in batch (RetryCount: {RetryCount})", batch.Count, retryCount);

                    // SUCCESS - Exit retry loop
                    break;
                }
                catch (DbUpdateConcurrencyException concurrencyEx)
                {
                    retryCount++;

                    if (retryCount >= maxRetries)
                    {
                        _logger.Error(concurrencyEx, "Concurrency conflict after {MaxRetries} retries. Logs will be retried in next batch.", maxRetries);

                        // Re-queue failed logs for next batch
                        foreach (var log in batch)
                            _logBatch.Enqueue(log);

                        break;
                    }

                    _logger.Warning(concurrencyEx, "Concurrency conflict detected in log batch processing (Retry {RetryCount}/{MaxRetries}). Retrying after {Delay}ms...", retryCount, maxRetries, retryDelay.TotalMilliseconds);

                    // Exponential backoff: 50ms, 100ms, 200ms
                    await Task.Delay(retryDelay, cancellationToken);
                    retryDelay = TimeSpan.FromMilliseconds(retryDelay.TotalMilliseconds * 2);
                }
                catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "40P01") // Deadlock
                {
                    _logger.Warning(pgEx, "Deadlock detected in log batch processing. Logs will be retried in next batch.");

                    // Re-queue logs for next batch
                    foreach (var log in batch)
                        _logBatch.Enqueue(log);

                    break;
                }
                catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "23503") // FK constraint violation
                {
                    _logger.Debug(pgEx, "FK constraint violation - occurrence not yet created. {Count} logs moved to pending queue.", batch.Count);

                    // Move to pending queue - occurrence might not be created yet
                    foreach (var log in batch)
                        _pendingLogs.Enqueue((log, 0, DateTime.UtcNow));

                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to process log batch");

                    // Re-queue logs for next batch
                    foreach (var log in batch)
                        _logBatch.Enqueue(log);

                    break;
                }
            }
        }
        finally
        {
            // Release the batch lock
            _batchLock.Release();
        }
    }

    /// <summary>
    /// Retries pending logs that were waiting for occurrence to be created.
    /// </summary>
    private async Task RetryPendingLogsAsync(CancellationToken cancellationToken)
    {
        if (_pendingLogs.IsEmpty)
            return;

        var logsToRetry = new List<WorkerLogMessage>();
        var logsToDiscard = new List<(WorkerLogMessage Log, int RetryCount, DateTime AddedAt)>();
        var logsToPutBack = new List<(WorkerLogMessage Log, int RetryCount, DateTime AddedAt)>();

        // Dequeue all pending logs
        while (_pendingLogs.TryDequeue(out var pending))
        {
            // Check if too old - discard
            if (DateTime.UtcNow - pending.AddedAt > _pendingMaxAge)
            {
                logsToDiscard.Add(pending);
                continue;
            }

            // Check if max retries exceeded - discard
            if (pending.RetryCount >= _maxPendingRetries)
            {
                logsToDiscard.Add(pending);
                continue;
            }

            logsToRetry.Add(pending.Log);
            logsToPutBack.Add((pending.Log, pending.RetryCount + 1, pending.AddedAt));
        }

        if (logsToDiscard.Count > 0)
            _logger.Warning("Discarded {Count} pending logs (max age or retries exceeded)", logsToDiscard.Count);

        if (logsToRetry.Count == 0)
            return;

        try
        {
            await using var scope = _serviceProvider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

            // Check which occurrences exist now
            var occurrenceIds = logsToRetry.Select(l => l.OccurrenceId).Distinct().ToList();

            var existingOccurrences = await dbContext.JobOccurrences.AsNoTracking()
                                                                    .Where(o => occurrenceIds.Contains(o.Id))
                                                                    .Select(o => o.Id)
                                                                    .ToListAsync(cancellationToken);

            var existingSet = existingOccurrences.ToHashSet();

            var logsWithOccurrence = new List<WorkerLogMessage>();
            var logsWithoutOccurrence = new List<(WorkerLogMessage Log, int RetryCount, DateTime AddedAt)>();

            for (var i = 0; i < logsToRetry.Count; i++)
            {
                var log = logsToRetry[i];
                var pending = logsToPutBack[i];

                if (existingSet.Contains(log.OccurrenceId))
                    logsWithOccurrence.Add(log);
                else
                    logsWithoutOccurrence.Add(pending);
            }

            // Put back logs that still don't have occurrence
            foreach (var pending in logsWithoutOccurrence)
                _pendingLogs.Enqueue(pending);

            // Insert logs that now have occurrence
            if (logsWithOccurrence.Count > 0)
            {
                var logsToInsert = logsWithOccurrence.Where(l => l.Log != null).Select(l => new JobOccurrenceLog
                {
                    Id = Guid.CreateVersion7(),
                    OccurrenceId = l.OccurrenceId,
                    Level = l.Log.Level,
                    Category = l.Log.Category,
                    ExceptionType = l.Log.ExceptionType,
                    Message = l.Log.Message,
                    Data = l.Log.Data,
                    Timestamp = l.Log.Timestamp,
                }).ToList();

                await dbContext.BulkInsertAsync(logsToInsert, cancellationToken: cancellationToken);

                _logger.Debug("Inserted {Count} pending logs (occurrence now exists)", logsWithOccurrence.Count);

                // Publish SignalR events
                var eventPublisher = scope.ServiceProvider.GetService<IJobOccurrenceEventPublisher>();
                if (eventPublisher != null)
                {
                    foreach (var log in logsWithOccurrence)
                        _ = eventPublisher.PublishLogAddedAsync(log.OccurrenceId, log.Log, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to retry pending logs. Will retry in next cycle.");

            // Put all back for retry
            foreach (var pending in logsToPutBack)
                _pendingLogs.Enqueue(pending);
        }
    }

    /// <summary>
    /// Stops the background service and cleans up resources.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.Information("LogCollectorService stopping...");

        try
        {
            // Process remaining logs before shutdown
            await ProcessBatchAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to process remaining logs during shutdown");
        }

        // Dispose semaphores
        _batchLock?.Dispose();
        _channelLock?.Dispose();

        // Close only channel (connection is managed by RabbitMQConnectionFactory)
        await _channel.SafeCloseAsync(_logger, cancellationToken);

        await base.StopAsync(cancellationToken);
        _logger.Information("LogCollectorService stopped");
    }
}
