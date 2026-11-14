using Milvasoft.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Dtos.AlertingDtos;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Interfaces.Messaging;
using Milvaion.Application.Interfaces.Secrets;
using Milvaion.Application.Utils.Constants;
using Milvaion.Infrastructure.BackgroundServices.Base;
using Milvaion.Infrastructure.Extensions;
using Milvaion.Infrastructure.Persistence.Context;
using Milvaion.Infrastructure.Services.Secrets;
using Milvaion.Infrastructure.Telemetry;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Helpers;
using Milvasoft.Milvaion.Sdk.Utils;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Milvaion.Infrastructure.BackgroundServices;

/// <summary>
/// Consumes worker logs from the message transport and appends them to JobOccurrence.Logs.
/// </summary>
public class LogCollectorService(IServiceProvider serviceProvider,
                                 IMessageConsumerTransport transport,
                                 IAlertNotifier alertNotifier,
                                 IOptions<LogCollectorOptions> logCollectorOptions,
                                 ILoggerFactory loggerFactory,
                                 BackgroundServiceMetrics metrics,
                                 IMemoryStatsRegistry memoryStatsRegistry = null) : MemoryTrackedBackgroundService(loggerFactory, logCollectorOptions.Value, memoryStatsRegistry)
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly ISecretMasker _secretMasker = serviceProvider.GetService<ISecretMasker>();
    private readonly IMessageConsumerTransport _transport = transport;
    private readonly IAlertNotifier _alertNotifier = alertNotifier;
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<LogCollectorService>();
    private readonly LogCollectorOptions _options = logCollectorOptions.Value;
    private readonly BackgroundServiceMetrics _metrics = metrics;
    private IMessageConsumer _consumer;

    // Batch processing
    private readonly ConcurrentQueue<LogItem> _logBatch = new();
    private readonly SemaphoreSlim _batchLock = new(1, 1);
    private const int _maxQueueSize = 100000; // Prevent unbounded memory growth

    // Pending logs - waiting for occurrence to be created (FK constraint race condition)
    private readonly ConcurrentQueue<(WorkerLogMessage Log, int RetryCount, DateTime AddedAt)> _pendingLogs = new();

    /// <summary>
    /// One received message. It may carry many logs and is acknowledged once the last of them is settled.
    /// </summary>
    private sealed class Delivery(IMessageDelivery message, int logCount)
    {
        private int _remaining = logCount;

        public IMessageDelivery Message { get; } = message;
        public bool SettleOne() => Interlocked.Decrement(ref _remaining) == 0;
    }

    private readonly record struct LogItem(WorkerLogMessage Message, Delivery Delivery);
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

                try
                {
                    await ProcessBatchAsync(stoppingToken);

                    // Retry pending logs (waiting for occurrence to be created)
                    await RetryPendingLogsAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // An escaped exception would end this loop silently while the consumer keeps filling the queue.
                    _logger.Error(ex, "Error in log batch loop");
                }

                TrackMemoryAfterIteration();
            }
        }, stoppingToken);

        await RabbitMQChannelExtensions.RunConsumerLoopAsync(ConnectAndConsumeAsync, _logger, ServiceName, failures =>
        {
            _alertNotifier.SendFireAndForget(AlertType.ServiceDegraded, new AlertPayload
            {
                Title = "LogCollector Service Degraded",
                Message = $"LogCollectorService failed to connect to {_transport.Name} after {failures} attempts. Logs are not being collected; the service keeps retrying.",
                Severity = AlertSeverity.Critical,
                Source = nameof(LogCollectorService),
                ThreadKey = "service-degraded-logcollector"
            });
        }, stoppingToken);
    }

    private async Task ConnectAndConsumeAsync(CancellationToken stoppingToken)
    {
        // Messages stay unacked until written, so the window has to cover at least a full batch.
        var prefetch = (ushort)Math.Clamp(_options.BatchSize * 2, 10, ushort.MaxValue);

        var consumer = await _transport.StartConsumerAsync(new ConsumerSubscription(WorkerConstant.Queues.WorkerLogs, prefetch, ServiceName),
                                                           delivery => ProcessLogMessageAsync(delivery, stoppingToken),
                                                           stoppingToken);

        _consumer = consumer;

        _logger.Information("LogCollectorService is now consuming messages...");

        // Keep running until cancellation or consumer death
        await consumer.Completion.WaitAsync(stoppingToken);

        await consumer.DisposeAsync();
    }

    private async Task ProcessLogMessageAsync(IMessageDelivery message, CancellationToken cancellationToken)
    {
        try
        {
            // Try to parse as batch message first (new format), then the single message format (backward compatibility)
            var batchMessage = JsonSerializer.Deserialize<WorkerLogBatchMessage>(message.Body.Span, ConstantJsonOptions.PropNameCaseInsensitive);

            List<WorkerLogMessage> logs;

            if (batchMessage != null && batchMessage.Count > 0)
            {
                logs = batchMessage.Logs;
            }
            else
            {
                var singleMessage = JsonSerializer.Deserialize<WorkerLogMessage>(message.Body.Span, ConstantJsonOptions.PropNameCaseInsensitive);

                if (singleMessage == null)
                {
                    _logger.Debug("Failed to deserialize log message");
                    await message.NackAsync(requeue: false, cancellationToken);
                    return;
                }

                logs = [singleMessage];
            }

            // Backpressure: losing logs is better than running out of memory
            if (_logBatch.Count >= _maxQueueSize)
            {
                _logger.Warning("Log batch queue is full ({Count} messages). Dropping message to prevent OOM.", _logBatch.Count);
                await message.AckAsync(cancellationToken);
                return;
            }

            // A worker can log a secret value it received in its job data; put the reference back before anything stores it.
            if (_secretMasker != null)
            {
                foreach (var log in logs)
                    _secretMasker.MaskLog(log?.Log);
            }

            // Add to batch queue (NO DB operation here!). The message is acknowledged after its logs are written.
            var delivery = new Delivery(message, logs.Count);

            foreach (var log in logs)
                _logBatch.Enqueue(new LogItem(log, delivery));

            // Trigger immediate batch if queue is full
            if (_logBatch.Count >= _options.BatchSize)
                await ProcessBatchAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to process log message");
            await message.NackAsync(requeue: false, cancellationToken);
        }
    }

    /// <summary>
    /// Process batch of logs - single DB transaction for all logs.
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

            var batch = new List<LogItem>();

            // Dequeue logs with max limit to prevent OOM from processing too many at once
            var maxBatchSize = Math.Min(_options.BatchSize * 10, 10000); // Max 10x batch size or 10k messages

            while (batch.Count < maxBatchSize && _logBatch.TryDequeue(out var item))
                batch.Add(item);

            if (batch.Count == 0)
                return;

            _metrics.SetLogBatchSize(batch.Count);

            try
            {
                await PersistLogsAsync(batch, cancellationToken);

                _metrics.RecordLogsCollected(batch.Count);
                _metrics.RecordLogBatchDuration(sw.Elapsed.TotalMilliseconds, batch.Count);

                _logger.Debug("Processed {Count} logs in batch", batch.Count);

                await SettleAsync(batch, cancellationToken);
            }
            catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "23503") // FK constraint violation
            {
                _logger.Debug(pgEx, "FK constraint violation - occurrence not yet created. {Count} logs moved to pending queue.", batch.Count);

                // Move to pending queue - occurrence might not be created yet
                foreach (var item in batch)
                    _pendingLogs.Enqueue((item.Message, 0, DateTime.UtcNow));

                await SettleAsync(batch, cancellationToken);
            }
            catch (Exception ex) when (RabbitMQChannelExtensions.IsPermanentDataError(ex))
            {
                _logger.Error(ex, "Log batch contains data the database rejects. Isolating the offending log(s).");

                await IsolatePoisonLogsAsync(batch, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to process log batch");

                // Re-queue logs for next batch (deadlocks and outages land here)
                foreach (var item in batch)
                    _logBatch.Enqueue(item);
            }
        }
        finally
        {
            // Release the batch lock
            _batchLock.Release();
        }
    }

    private async Task PersistLogsAsync(List<LogItem> batch, CancellationToken cancellationToken)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

        var messages = batch.Where(i => i.Message?.Log != null).Select(i => i.Message).ToList();

        if (messages.Count > 0)
        {
            var projectIds = await GetOccurrenceProjectIdsAsync(dbContext, messages, cancellationToken);

            var logsToInsert = messages.Select(m => ToEntity(m, projectIds)).ToList();

            await dbContext.BulkInsertAsync(logsToInsert, cancellationToken: cancellationToken);
        }

        // Trigger SignalR events after DB update
        var eventPublisher = scope.ServiceProvider.GetService<IJobOccurrenceEventPublisher>();

        if (eventPublisher == null)
            return;

        try
        {
            await Task.WhenAll(batch.Where(i => i.Message?.Log != null)
                                    .Select(i => eventPublisher.PublishLogAddedAsync(i.Message.OccurrenceId, i.Message.Log, cancellationToken)))
                      .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (TimeoutException)
        {
            _logger.Warning("SignalR event publishing timed out after 5 seconds for {Count} events", batch.Count);
        }
    }

    /// <summary>
    /// Workers do not send the project of a log line, so it is copied from the occurrence. A line whose occurrence does
    /// not exist (yet) keeps the Default project; its insert then fails on the occurrence key exactly as before projects.
    /// </summary>
    private static async Task<Dictionary<Guid, int>> GetOccurrenceProjectIdsAsync(MilvaionDbContext dbContext, List<WorkerLogMessage> messages, CancellationToken cancellationToken)
    {
        var occurrenceIds = messages.Select(m => m.OccurrenceId).Distinct().ToList();

        return await dbContext.JobOccurrences.AsNoTracking()
                                             .Where(o => occurrenceIds.Contains(o.Id))
                                             .Select(o => new { o.Id, o.ProjectId })
                                             .ToDictionaryAsync(o => o.Id, o => o.ProjectId, cancellationToken);
    }

    private static JobOccurrenceLog ToEntity(WorkerLogMessage message, Dictionary<Guid, int> projectIds) => new()
    {
        Id = Guid.CreateVersion7(),
        ProjectId = projectIds.GetValueOrDefault(message.OccurrenceId, ProjectDefaults.DefaultProjectId),
        OccurrenceId = message.OccurrenceId,
        Level = message.Log.Level,
        Category = message.Log.Category,
        ExceptionType = message.Log.ExceptionType,
        Message = message.Log.Message,
        Data = message.Log.Data,
        Timestamp = message.Log.Timestamp,
    };

    private async Task SettleAsync(List<LogItem> batch, CancellationToken cancellationToken)
    {
        foreach (var item in batch)
        {
            if (item.Delivery.SettleOne())
                await item.Delivery.Message.AckAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Writes a rejected batch one log at a time, so a single bad log cannot hold back the rest of the queue.
    /// </summary>
    private async Task IsolatePoisonLogsAsync(List<LogItem> batch, CancellationToken cancellationToken)
    {
        foreach (var item in batch)
        {
            try
            {
                await PersistLogsAsync([item], cancellationToken);
                await SettleAsync([item], cancellationToken);
            }
            catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "23503")
            {
                _pendingLogs.Enqueue((item.Message, 0, DateTime.UtcNow));

                await SettleAsync([item], cancellationToken);
            }
            catch (Exception ex) when (RabbitMQChannelExtensions.IsPermanentDataError(ex))
            {
                _logger.Error(ex, "Dropping log the database rejects. OccurrenceId: {OccurrenceId}", item.Message?.OccurrenceId);

                await SettleAsync([item], cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Log for occurrence {OccurrenceId} will be retried in the next batch", item.Message?.OccurrenceId);

                _logBatch.Enqueue(item);
            }
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
                                                                    .Select(o => new { o.Id, o.ProjectId })
                                                                    .ToDictionaryAsync(o => o.Id, o => o.ProjectId, cancellationToken);

            var existingSet = existingOccurrences.Keys.ToHashSet();

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
                var logsToInsert = logsWithOccurrence.Where(l => l.Log != null).Select(l => ToEntity(l, existingOccurrences)).ToList();

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

        // Cancels the consumer and batch loop and waits for them, so nothing else touches the batch below.
        await base.StopAsync(cancellationToken);

        try
        {
            // Process remaining logs before shutdown
            await ProcessBatchAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to process remaining logs during shutdown");
        }

        // Stop the consumer only now: the logs above still had to be acknowledged on it
        if (_consumer != null)
            await _consumer.DisposeAsync();

        // Dispose semaphores last: the work above still needs them
        _batchLock?.Dispose();

        _logger.Information("LogCollectorService stopped");
    }
}
