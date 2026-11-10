using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Dtos.AlertingDtos;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Interfaces.Redis;
using Milvaion.Application.Utils.Constants;
using Milvaion.Infrastructure.BackgroundServices.Base;
using Milvaion.Infrastructure.Extensions;
using Milvaion.Infrastructure.Persistence;
using Milvaion.Infrastructure.Persistence.Context;
using Milvaion.Infrastructure.Services.RabbitMQ;
using Milvaion.Infrastructure.Telemetry;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Helpers;
using Milvasoft.Milvaion.Sdk.Utils;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Diagnostics;
using System.Text.Json;

namespace Milvaion.Infrastructure.BackgroundServices;

/// <summary>
/// Consumes job status updates from RabbitMQ and updates JobOccurrence records.
/// </summary>
public class StatusTrackerService(IServiceProvider serviceProvider,
                                  IRedisSchedulerService redisScheduler,
                                  IRedisStatsService redisStatsService,
                                  IAlertNotifier alertNotifier,
                                  RabbitMQConnectionFactory rabbitMQFactory,
                                  IOptions<StatusTrackerOptions> options,
                                  IOptions<JobAutoDisableOptions> autoDisableOptions,
                                  IOptions<RabbitMQOptions> rabbitMQOptions,
                                  ILoggerFactory loggerFactory,
                                  BackgroundServiceMetrics metrics,
                                  IMemoryStatsRegistry memoryStatsRegistry = null) : MemoryTrackedBackgroundService(loggerFactory, options.Value, memoryStatsRegistry)
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IRedisSchedulerService _redisScheduler = redisScheduler;
    private readonly IRedisStatsService _redisStatsService = redisStatsService;
    private readonly IAlertNotifier _alertNotifier = alertNotifier;
    private readonly RabbitMQConnectionFactory _rabbitMQFactory = rabbitMQFactory;
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<StatusTrackerService>();
    private readonly StatusTrackerOptions _options = options.Value;
    private readonly JobAutoDisableOptions _autoDisableOptions = autoDisableOptions.Value;
    private readonly RabbitMQOptions _rabbitMQOptions = rabbitMQOptions.Value;
    private readonly BackgroundServiceMetrics _metrics = metrics;
    private IChannel _channel;

    // Channel thread-safety: IChannel is NOT thread-safe, serialize ACK/NACK operations
    private readonly SemaphoreSlim _channelLock = new(1, 1);

    private static readonly JobOccurrenceStatus[] _finalStatuses =
    [
        JobOccurrenceStatus.Completed,
        JobOccurrenceStatus.Failed,
        JobOccurrenceStatus.Cancelled,
        JobOccurrenceStatus.TimedOut
    ];

    private static readonly JobOccurrenceStatus[] _nonFinalStatuses =
    [
        JobOccurrenceStatus.Queued,
        JobOccurrenceStatus.Running
    ];

    //  Batch processing — stores the RabbitMQ delivery tag alongside each message so the
    //  ACK can be deferred until after the batch is persisted to the database.
    //  The channel travels with the tag: a delivery tag is only valid on the channel that delivered it.
    private readonly System.Collections.Concurrent.ConcurrentQueue<BatchItem> _statusBatch = new();
    private readonly SemaphoreSlim _batchLock = new(1, 1);

    // Retry queue for Redis completion updates that failed or timed out.
    // MarkJobAsCompletedAsync is idempotent (SREM), so retrying is safe.
    private readonly System.Collections.Concurrent.ConcurrentQueue<Guid> _pendingRedisCompletions = new();
    private readonly static List<string> _updatePropNames =
    [
        nameof(JobOccurrence.Status),
        nameof(JobOccurrence.WorkerId),
        nameof(JobOccurrence.StartTime),
        nameof(JobOccurrence.EndTime),
        nameof(JobOccurrence.DurationMs),
        nameof(JobOccurrence.Result),
        nameof(JobOccurrence.Exception),
        nameof(JobOccurrence.LastHeartbeat),
        nameof(JobOccurrence.StatusChangeLogs),
        nameof(JobOccurrence.StepStatus),
    ];

    private readonly static List<string> _jobCircuitBreakerUpdateProps =
    [
        nameof(ScheduledJob.AutoDisableSettings),
        nameof(ScheduledJob.IsActive)
    ];

    /// <inheritdoc/>
    protected override string ServiceName => "StatusTracker";

    /// <inheritdoc />
    protected override async Task ExecuteWithMemoryTrackingAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.Warning("Status tracking is disabled. Skipping startup.");

            return;
        }

        _logger.Information("Status tracking is starting...");

        // Start batch processor task
        var batchTask = Task.Run(async () =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using var _ = _metrics.MeasureDuration(ServiceName);

                try
                {
                    await Task.Delay(_options.BatchIntervalMs, stoppingToken);

                    await ProcessBatchAsync(stoppingToken);

                    // Retry failed Redis completions independently of batch processing.
                    await DrainPendingRedisCompletionsAsync(stoppingToken);

                    _metrics.RecordServiceIteration(ServiceName);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _metrics.RecordServiceError(ServiceName, ex.GetType().Name);
                    _logger.Error(ex, "Error in batch processor loop");
                }
                finally
                {
                    TrackMemoryAfterIteration();
                }
            }
        }, stoppingToken);

        await RabbitMQChannelExtensions.RunConsumerLoopAsync(ConnectAndConsumeAsync, _logger, ServiceName, failures =>
        {
            _metrics.RecordServiceError(ServiceName, "RabbitMQ_Connection_Failed");

            _alertNotifier.SendFireAndForget(AlertType.ServiceDegraded, new AlertPayload
            {
                Title = "StatusTracker Service Degraded",
                Message = $"StatusTrackerService failed to connect to RabbitMQ after {failures} attempts. Status updates are not being processed; the service keeps retrying.",
                Severity = AlertSeverity.Critical,
                Source = nameof(StatusTrackerService),
                ThreadKey = "service-degraded-statustracker"
            });
        }, stoppingToken);
    }

    private async Task ConnectAndConsumeAsync(CancellationToken stoppingToken)
    {
        // Get channel from shared connection factory
        var channel = await _rabbitMQFactory.CreateChannelAsync(stoppingToken);

        _channel = channel;

        // Subscribe to channel events
        _channel.ChannelShutdownAsync += async (sender, args) =>
        {
            _logger.Warning("StatusTracker RabbitMQ channel shutdown. Reason: {Reason}", args.ReplyText);
            await Task.CompletedTask;
        };

        // Declare queue (idempotent)
        await _channel.QueueDeclareAsync(queue: WorkerConstant.Queues.StatusUpdates,
                                         durable: true,
                                         exclusive: false,
                                         autoDelete: false,
                                         arguments: _rabbitMQOptions.BuildQueueArguments(),
                                         cancellationToken: stoppingToken);

        // Set prefetch count
        // Twice the batch size, so a batch can fill up while the previous one is being written.
        var prefetch = (ushort)Math.Clamp(_options.BatchSize * 2, 10, ushort.MaxValue);

        await _channel.BasicQosAsync(0, prefetch, false, stoppingToken);

        _logger.Information("StatusTracker connected to RabbitMQ (shared connection). Queue: {Queue}, ChannelNumber: {ChannelNumber}", WorkerConstant.Queues.StatusUpdates, _channel.ChannelNumber);

        // Setup consumer with all event handlers
        var consumer = new AsyncEventingBasicConsumer(_channel);

        consumer.ReceivedAsync += async (model, ea) =>
        {
            try
            {
                await ProcessStatusUpdateAsync(ea, channel, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error in StatusTracker consumer handler for OccurrenceId: {OccurrenceId}", ea.BasicProperties?.CorrelationId);
            }
        };

        consumer.ShutdownAsync += async (sender, args) =>
        {
            _logger.Warning("StatusTracker consumer shutdown. Reason: {Reason}", args.ReplyText);
            await Task.CompletedTask;
        };

        consumer.RegisteredAsync += async (sender, args) =>
        {
            _logger.Information("StatusTracker consumer registered. ConsumerTags: {Tags}", string.Join(", ", args.ConsumerTags));
            await Task.CompletedTask;
        };

        consumer.UnregisteredAsync += async (sender, args) =>
        {
            _logger.Warning("StatusTracker consumer unregistered. ConsumerTags: {Tags}", string.Join(", ", args.ConsumerTags));
            await Task.CompletedTask;
        };

        var consumerTag = await _channel.BasicConsumeAsync(queue: WorkerConstant.Queues.StatusUpdates,
                                                           autoAck: false,
                                                           consumer: consumer,
                                                           cancellationToken: stoppingToken);

        _logger.Information("StatusTrackerService consuming from {Queue}. ConsumerTag: {ConsumerTag}, ChannelOpen: {IsOpen}, ChannelNumber: {ChannelNumber}", WorkerConstant.Queues.StatusUpdates, consumerTag, _channel.IsOpen, _channel.ChannelNumber);

        // Keep running until cancellation or channel death
        await channel.WaitForShutdownAsync(stoppingToken);

        await channel.SafeCloseAsync(_logger, CancellationToken.None);
    }

    private async Task ProcessStatusUpdateAsync(BasicDeliverEventArgs ea, IChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            // Parse message
            var message = JsonSerializer.Deserialize<JobStatusUpdateMessage>(ea.Body.Span, ConstantJsonOptions.PropNameCaseInsensitive);

            if (message == null)
            {
                _logger.Debug("Failed to deserialize status update message");
                _metrics.RecordStatusUpdateFailure("Deserialization_Failed");

                await channel.SafeNackAsync(ea.DeliveryTag, _channelLock, _logger, cancellationToken);

                return;
            }

            //  ONLY mark as Running immediately (for dispatcher checks)
            if (message.Status == JobOccurrenceStatus.Running)
            {
                try
                {
                    await _redisScheduler.TryMarkJobAsRunningAsync(message.JobId, message.OccurrenceId, cancellationToken);
                    _logger.Debug("Job {JobId} immediately marked as running in Redis (OccurrenceId: {OccurrenceId})", message.JobId, message.OccurrenceId);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to immediately mark job {JobId} as running in Redis (non-critical)", message.JobId);
                }
            }

            //  Add to batch queue together with its delivery tag (NO DB operation here!).
            //  ACK is deferred until ProcessBatchAsync persists the batch to the database,
            //  guaranteeing at-least-once delivery if the app crashes before the DB write.
            _statusBatch.Enqueue(new BatchItem(message, ea.DeliveryTag, channel));

            //  Trigger immediate batch if full
            if (_statusBatch.Count >= _options.BatchSize)
                await ProcessBatchAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _metrics.RecordStatusUpdateFailure(ex.GetType().Name);
            _logger.Error(ex, "Failed to process status update message");

            await channel.SafeNackAsync(ea.DeliveryTag, _channelLock, _logger, cancellationToken);
        }
    }

    /// <summary>
    /// Process batch of status updates - single DB transaction.
    /// </summary>
    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        if (_statusBatch.IsEmpty)
            return;

        var sw = Stopwatch.StartNew();

        //  Use semaphore to ensure single access
        await _batchLock.WaitAsync(cancellationToken);

        try
        {
            if (_statusBatch.IsEmpty)
                return;

            var batch = DequeueBatch();

            if (batch.IsNullOrEmpty())
                return;

            _metrics.SetStatusBatchSize(batch.Count);

            try
            {
                await PersistBatchAndAckAsync(batch, sw, cancellationToken);
            }
            catch (Exception ex) when (RabbitMQChannelExtensions.IsPermanentDataError(ex))
            {
                _logger.Error(ex, "Status update batch contains data the database rejects. Isolating the offending message(s).");

                await IsolatePoisonMessagesAsync(batch, cancellationToken);
            }
            catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "40P01") // Deadlock
            {
                _metrics.RecordStatusUpdateFailure("Database_Deadlock");
                _logger.Warning(pgEx, "Deadlock detected in status batch processing. Updates will be retried in next batch.");

                RequeueMessages(batch);
            }
            catch (Exception ex)
            {
                _metrics.RecordStatusUpdateFailure(ex.GetType().Name);
                _logger.Error(ex, "Failed to process status update batch");

                RequeueMessages(batch);
            }
        }
        finally
        {
            _batchLock.Release();
        }
    }

    /// <summary>
    /// Persists a batch and applies its side effects. Messages are acknowledged only after the write.
    /// </summary>
    private async Task PersistBatchAndAckAsync(List<BatchItem> batch, Stopwatch sw, CancellationToken cancellationToken)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

        // Deduplicate by OccurrenceId (message.OccurrenceId is Occurrence.Id)
        var statusByOccurrenceId = DeduplicateByOccurrenceId(batch);
        var occurrenceIds = statusByOccurrenceId.Keys.ToList();

        _logger.Debug("Processing batch: {Count} unique status updates. Sample OccurrenceIds: {Samples}", occurrenceIds.Count, string.Join(", ", occurrenceIds.Take(3)));

        //  Single query for all occurrences (ORDER BY Id to prevent deadlock)
        var occurrences = await FetchOccurrencesAsync(dbContext, occurrenceIds, cancellationToken);

        if (occurrences.IsNullOrEmpty())
        {
            _logger.Warning("No matching occurrences found for {Count} status updates. OccurrenceIds: {Ids}", batch.Count, string.Join(", ", occurrenceIds.Take(5)));

            // ACK the stale messages so they don't block the queue — there is nothing to persist.
            await AckAsync(batch, cancellationToken);

            return;
        }

        // Apply status updates and collect side effects
        var updateResult = await ApplyStatusUpdatesAsync(occurrences, statusByOccurrenceId, cancellationToken);

        // Persist changes to database
        await PersistOccurrenceChangesAsync(dbContext, occurrences, cancellationToken);

        // Process circuit breaker updates for jobs
        if (updateResult.CircuitBreakerUpdates.Count > 0 && _autoDisableOptions.Enabled)
            await ProcessCircuitBreakerUpdatesAsync(dbContext, updateResult.CircuitBreakerUpdates, cancellationToken);

        // Batch update consumer counters (SINGLE Redis Lua script call!)
        if (updateResult.ConsumerCounterUpdates.Count > 0)
            await UpdateConsumerCountersBatchAsync(updateResult.ConsumerCounterUpdates, cancellationToken);

        // Update Redis state (running/completed markers)
        await UpdateRedisStateAsync(occurrences, cancellationToken);

        // Publish SignalR events
        await PublishSignalREventsAsync(scope, occurrences, cancellationToken);

        // Send job execution failed alerts (fire-and-forget)
        SendJobExecutionFailedAlerts(updateResult.FailedOccurrences);

        // Record metrics
        RecordBatchMetrics(sw, batch.Count, occurrences);

        _logger.Debug("Processed {Count} status updates in batch", batch.Count);

        // ACK all messages only after the batch has been successfully persisted to the database.
        await AckAsync(batch, cancellationToken);
    }

    private async Task AckAsync(List<BatchItem> batch, CancellationToken cancellationToken)
    {
        foreach (var item in batch)
            await item.Channel.SafeAckAsync(item.DeliveryTag, _channelLock, _logger, cancellationToken);
    }

    /// <summary>
    /// Writes the messages of a rejected batch one at a time, so one bad message cannot hold back the others.
    /// Only messages the database rejects outright are dropped; a transient failure goes back to the batch queue.
    /// </summary>
    private async Task IsolatePoisonMessagesAsync(List<BatchItem> batch, CancellationToken cancellationToken)
    {
        foreach (var item in batch)
        {
            try
            {
                await PersistBatchAndAckAsync([item], Stopwatch.StartNew(), cancellationToken);
            }
            catch (Exception ex) when (RabbitMQChannelExtensions.IsPermanentDataError(ex))
            {
                _metrics.RecordStatusUpdateFailure("Poison_Message");
                _logger.Error(ex, "Dropping status update the database rejects. OccurrenceId: {OccurrenceId}, Status: {Status}", item.Message.OccurrenceId, item.Message.Status);

                await item.Channel.SafeNackAsync(item.DeliveryTag, _channelLock, _logger, cancellationToken, requeue: false);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Status update for occurrence {OccurrenceId} will be retried in the next batch", item.Message.OccurrenceId);

                _statusBatch.Enqueue(item);
            }
        }
    }

    /// <summary>
    /// Dequeues all messages (with their delivery tags) from the batch queue.
    /// </summary>
    private List<BatchItem> DequeueBatch()
    {
        var batch = new List<BatchItem>();

        while (_statusBatch.TryDequeue(out var item))
            batch.Add(item);

        return batch;
    }

    /// <summary>
    /// Re-queues messages back to the batch queue for retry (delivery tags preserved — NOT ACK'd).
    /// </summary>
    private void RequeueMessages(List<BatchItem> batch)
    {
        foreach (var item in batch)
            _statusBatch.Enqueue(item);
    }

    /// <summary>
    /// Deduplicates messages by OccurrenceId (last update wins).
    /// </summary>
    private static Dictionary<Guid, JobStatusUpdateMessage> DeduplicateByOccurrenceId(List<BatchItem> batch)
    {
        var statusByOccurrenceId = new Dictionary<Guid, JobStatusUpdateMessage>(batch.Count);

        foreach (var item in batch)
            statusByOccurrenceId[item.Message.OccurrenceId] = item.Message; // Last update wins

        return statusByOccurrenceId;
    }

    /// <summary>
    /// Fetches occurrences from database by occurrence IDs (message.OccurrenceId is Occurrence.Id).
    /// </summary>
    private static Task<List<JobOccurrence>> FetchOccurrencesAsync(MilvaionDbContext dbContext, List<Guid> occurrenceIds, CancellationToken cancellationToken)
        => dbContext.JobOccurrences
                    .AsNoTracking()
                    .Where(o => occurrenceIds.Contains(o.Id))
                    .OrderBy(o => o.Id) // Consistent lock order
                    .Select(JobOccurrence.Projections.UpdateStatus)
                    .ToListAsync(cancellationToken: cancellationToken);

    /// <summary>
    /// Applies status updates to occurrences in memory and collects side effects.
    /// Returns circuit breaker updates and consumer counter updates for batch processing.
    /// </summary>
    private async Task<StatusUpdateResult> ApplyStatusUpdatesAsync(List<JobOccurrence> occurrences, Dictionary<Guid, JobStatusUpdateMessage> statusByOccurrenceId, CancellationToken cancellationToken)
    {
        var occurrenceDict = occurrences.ToDictionary(o => o.Id); // Match by Occurrence.Id
        var result = new StatusUpdateResult();

        //  Update in memory (NO foreach DB operation!)
        foreach (var kvp in statusByOccurrenceId)
        {
            if (!occurrenceDict.TryGetValue(kvp.Key, out var occurrence))
            {
                _logger.Debug("JobOccurrence not found for OccurrenceId: {OccurrenceId}", kvp.Key);
                continue;
            }

            var message = kvp.Value;

            // Track status change history
            var previousStatus = occurrence.Status;
            var newStatus = message.Status;

            // Prevent invalid status transitions from FINAL states
            // Once a job reaches a terminal state (Completed, Failed, Cancelled, TimedOut), it should NOT transition back to Running or Queued (race condition from late messages)
            if (_finalStatuses.Contains(previousStatus) && _nonFinalStatuses.Contains(newStatus))
                continue;

            // Detect if this is a heartbeat-only update (no status change, no other data)
            var isHeartbeat = previousStatus == newStatus
                              && newStatus == JobOccurrenceStatus.Running
                              && message.EndTime == null
                              && message.DurationMs == null
                              && string.IsNullOrEmpty(message.Result);

            if (isHeartbeat)
            {
                occurrence.LastHeartbeat = DateTime.UtcNow;

                _logger.Debug("Heartbeat received for OccurrenceId: {OccurrenceId}", kvp.Key);

                // No need to update Redis counters for heartbeat (status hasn't changed)
                continue; // Skip other processing
            }

            occurrence.StatusChangeLogs ??= [];

            // Limit status change logs to prevent unbounded growth
            if (occurrence.StatusChangeLogs.Count >= _options.ExecutionLogMaxCount)
                occurrence.StatusChangeLogs = [.. occurrence.StatusChangeLogs.OrderByDescending(l => l.Timestamp).Take(_options.ExecutionLogMaxCount)];

            if (previousStatus != newStatus)
                occurrence.StatusChangeLogs.Add(new OccurrenceStatusChangeLog
                {
                    Timestamp = DateTime.UtcNow,
                    From = previousStatus,
                    To = newStatus
                });

            // Collect consumer counter updates for batch processing
            if (!string.IsNullOrEmpty(message.WorkerId) && !string.IsNullOrEmpty(message.InstanceId) && !string.IsNullOrEmpty(occurrence.JobName) && previousStatus != newStatus)
            {
                result.ConsumerCounterUpdates.Add(new ConsumerCounterUpdate(message.WorkerId, message.InstanceId, occurrence.JobName, previousStatus, newStatus));
            }

            // Update real-time statistics counters for dashboard (SYNCHRONOUS for critical counters)
            if (previousStatus != newStatus)
            {
                try
                {
                    await _redisStatsService.UpdateStatusCountersAsync(previousStatus, newStatus, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to update Redis stats counters. Dashboard stats may be stale. Status: {Old} -> {New}, OccurrenceId: {OccurrenceId}", previousStatus, newStatus, occurrence.Id);
                }
            }

            // Track for circuit breaker update
            if (newStatus == JobOccurrenceStatus.Failed)
            {
                result.CircuitBreakerUpdates[occurrence.JobId] = new CircuitBreakerUpdate(true, message.Exception ?? "Unknown error");

                // Send job execution failed alert
                result.FailedOccurrences.Add(new FailedOccurrenceInfo(occurrence.JobId, occurrence.JobName, message.OccurrenceId, message.Exception));
            }
            else if (newStatus == JobOccurrenceStatus.Completed)
            {
                result.CircuitBreakerUpdates[occurrence.JobId] = new CircuitBreakerUpdate(false, null);
            }

            // Apply field updates
            occurrence.Status = message.Status;

            if (!string.IsNullOrEmpty(message.WorkerId))
                occurrence.WorkerId = message.WorkerId;

            if (message.StartTime.HasValue)
                occurrence.StartTime = message.StartTime;

            if (message.EndTime.HasValue)
                occurrence.EndTime = message.EndTime;

            if (message.DurationMs.HasValue)
            {
                occurrence.DurationMs = message.DurationMs;

                // Track duration for average calculation (only for completed jobs)
                if (newStatus == JobOccurrenceStatus.Completed)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _redisStatsService.TrackDurationAsync(message.DurationMs.Value, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug(ex, "Failed to track duration (non-critical)");
                        }
                    }, CancellationToken.None);
                }
            }

            if (!string.IsNullOrEmpty(message.Result))
                occurrence.Result = message.Result;

            // Smart exception handling: Clear old exception if job completed successfully
            if (!string.IsNullOrEmpty(message.Exception))
            {
                occurrence.Exception = message.Exception;
            }
            else if (message.Status == JobOccurrenceStatus.Completed)
            {
                // Job completed successfully - clear any previous exception
                occurrence.Exception = null;
            }

            occurrence.LastHeartbeat = DateTime.UtcNow;

            // Mirror orchestration status for workflow step occurrences so WorkflowEngineService
            // can read StepStatus directly without a separate sync query.
            if (occurrence.WorkflowRunId != null)
            {
                occurrence.StepStatus = newStatus switch
                {
                    JobOccurrenceStatus.Completed => WorkflowStepStatus.Completed,
                    JobOccurrenceStatus.Failed or JobOccurrenceStatus.TimedOut or JobOccurrenceStatus.Unknown => WorkflowStepStatus.Failed,
                    JobOccurrenceStatus.Cancelled => WorkflowStepStatus.Cancelled,
                    _ => occurrence.StepStatus
                };
            }
        }

        return result;
    }

    /// <summary>
    /// Persists occurrence changes. The status read for this batch may be out of date by now (another instance,
    /// or the zombie detector), so a late non-final update is checked against the locked row: it must never
    /// overwrite a final status. Rows rejected that way are removed from <paramref name="occurrences"/>.
    /// </summary>
    private static async Task PersistOccurrenceChangesAsync(MilvaionDbContext dbContext,
                                                            List<JobOccurrence> occurrences,
                                                            CancellationToken cancellationToken)
    {
        var rejected = await dbContext.BulkUpdateGuardedAsync(occurrences,
                                                              (occurrence, row) => !(_finalStatuses.Contains(row.Status) && _nonFinalStatuses.Contains(occurrence.Status)),
                                                              bc => bc.PropertiesToIncludeOnUpdate = _updatePropNames,
                                                              cancellationToken);

        if (rejected.Count > 0)
        {
            var rejectedIds = rejected.Select(o => o.Id).ToHashSet();

            occurrences.RemoveAll(o => rejectedIds.Contains(o.Id));
        }
    }

    /// <summary>
    /// Drains and retries pending Redis completion updates that failed in previous batches.
    /// Runs independently so retries happen even when no new status messages arrive.
    /// </summary>
    private async Task DrainPendingRedisCompletionsAsync(CancellationToken cancellationToken)
    {
        if (_pendingRedisCompletions.IsEmpty)
            return;

        var pending = new List<Guid>();

        while (_pendingRedisCompletions.TryDequeue(out var jobId))
            pending.Add(jobId);

        if (pending.Count == 0)
            return;

        _logger.Debug("Retrying {Count} pending Redis completion(s)...", pending.Count);

        var stillFailed = new System.Collections.Concurrent.ConcurrentBag<Guid>();

        foreach (var jobId in pending)
        {
            try
            {
                var removed = await _redisScheduler.MarkJobAsCompletedAsync(jobId, cancellationToken);

                if (!removed)
                    stillFailed.Add(jobId);
                else
                    _logger.Debug("Pending Redis completion succeeded for job {JobId}", jobId);
            }
            catch (Exception ex)
            {
                stillFailed.Add(jobId);
                _logger.Warning(ex, "Pending Redis completion retry failed for job {JobId}", jobId);
            }
        }

        // Re-queue any that still failed
        foreach (var jobId in stillFailed)
            _pendingRedisCompletions.Enqueue(jobId);

        if (!stillFailed.IsEmpty)
            _logger.Warning("{Count} Redis completion(s) still pending after retry. JobIds: {JobIds}",
                stillFailed.Count, string.Join(", ", stillFailed.Take(5)));
    }

    /// <summary>
    /// Updates Redis state for running/completed jobs.
    /// Failed/timed-out completion updates are re-queued and retried in the next batch.
    /// MarkJobAsCompletedAsync (SREM) is idempotent, so retrying duplicates is safe.
    /// </summary>
    private async Task UpdateRedisStateAsync(List<JobOccurrence> occurrences, CancellationToken cancellationToken)
    {
        // Batch Redis updates efficiently with deduplication
        // Group by JobId to avoid duplicate Redis calls for same job
        var redisUpdates = new Dictionary<Guid, (JobOccurrenceStatus status, Guid correlationId)>();

        // Drain pending completions from previous failed/timed-out attempts
        while (_pendingRedisCompletions.TryDequeue(out var pendingJobId))
        {
            redisUpdates.TryAdd(pendingJobId, (JobOccurrenceStatus.Completed, Guid.Empty));
        }

        foreach (var occurrence in occurrences)
        {
            // Last update wins - if same JobId appears multiple times, use latest status
            if (occurrence.Status == JobOccurrenceStatus.Running || occurrence.Status.IsFinalStatus())
            {
                redisUpdates[occurrence.JobId] = (occurrence.Status, occurrence.CorrelationId);
            }
        }

        // Execute deduplicated Redis updates in parallel with timeout
        if (redisUpdates.IsNullOrEmpty())
            return;

        var failedCompletions = new System.Collections.Concurrent.ConcurrentBag<Guid>();

        var redisUpdateTasks = redisUpdates.Select(kvp => Task.Run(async () =>
        {
            var (status, correlationId) = kvp.Value;
            var jobId = kvp.Key;

            try
            {
                if (status == JobOccurrenceStatus.Running)
                {
                    await _redisScheduler.TryMarkJobAsRunningAsync(jobId, correlationId, cancellationToken);
                }
                else if (status.IsFinalStatus())
                {
                    var removed = await _redisScheduler.MarkJobAsCompletedAsync(jobId, cancellationToken);

                    if (!removed)
                        failedCompletions.Add(jobId);
                }
            }
            catch (Exception ex)
            {
                if (status.IsFinalStatus())
                    failedCompletions.Add(jobId);

                _logger.Warning(ex, "Failed to update Redis state for job {JobId} (status: {Status}). Will retry in next batch.", jobId, status);
            }
        }, cancellationToken)).ToList();

        try
        {
            await Task.WhenAll(redisUpdateTasks).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        }
        catch (TimeoutException)
        {
            // Timeout - re-queue all pending completion updates (idempotent, safe to retry)
            foreach (var kvp in redisUpdates.Where(u => u.Value.status.IsFinalStatus()))
                failedCompletions.Add(kvp.Key);

            _logger.Warning("Redis state updates timed out after 3s. {Count} completion(s) queued for retry.", failedCompletions.Count);
        }

        // Re-queue failed completions for next batch cycle
        foreach (var jobId in failedCompletions)
            _pendingRedisCompletions.Enqueue(jobId);

        if (!failedCompletions.IsEmpty)
            _logger.Warning("{Count} Redis completion update(s) will be retried in next batch. JobIds: {JobIds}",
                failedCompletions.Count, string.Join(", ", failedCompletions.Take(5)));
    }

    /// <summary>
    /// Publishes SignalR events for updated occurrences.
    /// </summary>
    private Task PublishSignalREventsAsync(AsyncServiceScope scope, List<JobOccurrence> occurrences, CancellationToken cancellationToken)
    {
        var eventPublisher = scope.ServiceProvider.GetService<IJobOccurrenceEventPublisher>();

        return eventPublisher.PublishOccurrenceUpdatedAsync(occurrences, _logger, cancellationToken);
    }

    /// <summary>
    /// Records batch processing metrics.
    /// </summary>
    private void RecordBatchMetrics(Stopwatch sw, int batchCount, List<JobOccurrence> occurrences)
    {
        _metrics.RecordStatusUpdatesProcessed(batchCount);
        _metrics.RecordStatusUpdateDuration(sw.Elapsed.TotalMilliseconds, batchCount);

        // Record status distribution
        foreach (var occ in occurrences)
            _metrics.RecordStatusUpdateByStatus(occ.Status.ToString());
    }

    /// <summary>
    /// Processes circuit breaker updates for jobs that completed or failed.
    /// Increments failure count on failure, resets on success, auto-disables if threshold reached.
    /// </summary>
    private async Task ProcessCircuitBreakerUpdatesAsync(MilvaionDbContext dbContext, Dictionary<Guid, CircuitBreakerUpdate> jobUpdates, CancellationToken cancellationToken)
    {
        try
        {
            var jobIds = jobUpdates.Keys.ToList();

            // Fetch jobs that need circuit breaker updates
            var jobs = await dbContext.ScheduledJobs
                                      .AsNoTracking()
                                      .Where(j => jobIds.Contains(j.Id))
                                      .Select(ScheduledJob.Projections.CircuitBreaker)
                                      .ToListAsync(cancellationToken);

            if (jobs.Count == 0)
                return;

            var jobsToUpdate = new List<ScheduledJob>();
            var autoDisabledJobs = new List<ScheduledJob>();

            foreach (var job in jobs)
            {
                var failureWindowThreshold = DateTime.UtcNow.AddMinutes(-(job.AutoDisableSettings.FailureWindowMinutes ?? _autoDisableOptions.FailureWindowMinutes));
                var update = jobUpdates[job.Id];

                // Check if auto-disable is enabled for this job
                var isAutoDisableEnabled = job.AutoDisableSettings.Enabled ?? true; // Use job-specific or default to true

                if (!isAutoDisableEnabled)
                {
                    // Job explicitly disabled auto-disable, just track failures but don't disable
                    if (update.IsFailed)
                    {
                        job.AutoDisableSettings.ConsecutiveFailureCount++;
                        job.AutoDisableSettings.LastFailureTime = DateTime.UtcNow;
                        jobsToUpdate.Add(job);
                    }
                    else
                    {
                        // Reset on success
                        if (job.AutoDisableSettings.ConsecutiveFailureCount > 0)
                        {
                            job.AutoDisableSettings.ConsecutiveFailureCount = 0;
                            job.AutoDisableSettings.LastFailureTime = null;
                            jobsToUpdate.Add(job);
                        }
                    }

                    continue;
                }

                if (update.IsFailed)
                {
                    // Check if last failure is within the window, otherwise reset counter
                    if (job.AutoDisableSettings.LastFailureTime.HasValue && job.AutoDisableSettings.LastFailureTime.Value < failureWindowThreshold)
                    {
                        // Old failures, reset counter
                        job.AutoDisableSettings.ConsecutiveFailureCount = 1;
                    }
                    else
                    {
                        job.AutoDisableSettings.ConsecutiveFailureCount++;
                    }

                    job.AutoDisableSettings.LastFailureTime = DateTime.UtcNow;

                    // Get threshold (job-specific or global)
                    var threshold = job.AutoDisableSettings.Threshold ?? _autoDisableOptions.ConsecutiveFailureThreshold;

                    // Check if threshold reached
                    if (job.AutoDisableSettings.ConsecutiveFailureCount >= threshold && job.IsActive)
                    {
                        job.IsActive = false;
                        job.AutoDisableSettings.DisabledAt = DateTime.UtcNow;
                        job.AutoDisableSettings.DisableReason = $"Auto-disabled after {job.AutoDisableSettings.ConsecutiveFailureCount} consecutive failures. Last error: {TruncateException(update.Exception, 200)}";

                        autoDisabledJobs.Add(job);

                        _logger.Warning("Job {JobId} ({JobName}) auto-disabled after {FailureCount} consecutive failures", job.Id, job.DisplayName ?? job.JobNameInWorker, job.AutoDisableSettings.ConsecutiveFailureCount);
                    }

                    jobsToUpdate.Add(job);
                }
                else
                {
                    // Success - reset failure counter
                    if (job.AutoDisableSettings.ConsecutiveFailureCount > 0 || job.AutoDisableSettings.DisabledAt.HasValue)
                    {
                        _logger.Debug("Job {JobId} completed successfully, resetting failure counter (was {Count})", job.Id, job.AutoDisableSettings.ConsecutiveFailureCount);

                        job.AutoDisableSettings.ConsecutiveFailureCount = 0;
                        job.AutoDisableSettings.LastFailureTime = null;

                        // Don't clear AutoDisabledAt - keep for history
                        jobsToUpdate.Add(job);
                    }
                }
            }

            // Bulk update all jobs
            if (jobsToUpdate.Count > 0)
            {
                await dbContext.BulkUpdateAsync(jobsToUpdate, (bc) =>
                {
                    bc.PropertiesToInclude = bc.PropertiesToIncludeOnUpdate = _jobCircuitBreakerUpdateProps;
                }, cancellationToken: cancellationToken);

                _logger.Debug("Updated circuit breaker state for {Count} jobs", jobsToUpdate.Count);
            }

            // Remove auto-disabled jobs from Redis scheduler (BATCH operation)
            if (autoDisabledJobs.Count > 0)
            {
                try
                {
                    var autoDisabledJobIds = autoDisabledJobs.Select(j => j.Id).ToList();

                    // Batch remove from scheduled set (single ZREM call)
                    await _redisScheduler.RemoveFromScheduledSetBulkAsync(autoDisabledJobIds, cancellationToken);

                    // Batch remove from cache (single pipeline)
                    await _redisScheduler.RemoveCachedJobsBulkAsync(autoDisabledJobIds, cancellationToken);

                    _logger.Debug("Removed {Count} auto-disabled jobs from Redis scheduler in batch", autoDisabledJobs.Count);

                    // Send alerts for auto-disabled jobs (fire-and-forget, non-blocking)
                    SendAutoDisabledJobAlerts(autoDisabledJobs);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to remove auto-disabled jobs from Redis (non-critical)");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to process circuit breaker updates (non-critical)");
        }
    }

    private static string TruncateException(string exception, int maxLength)
    {
        if (string.IsNullOrEmpty(exception))
            return "Unknown error";

        return exception.Length <= maxLength ? exception : exception[..(maxLength - 3)] + "...";
    }

    /// <summary>
    /// Sends alerts for auto-disabled jobs using fire-and-forget pattern.
    /// This method returns immediately and never blocks the main processing flow.
    /// </summary>
    private void SendAutoDisabledJobAlerts(List<ScheduledJob> autoDisabledJobs)
    {
        foreach (var job in autoDisabledJobs)
        {
            _alertNotifier.SendFireAndForget(AlertType.JobAutoDisabled, new AlertPayload
            {
                Title = "Job Auto-Disabled",
                Message = $"Job '{job.DisplayName ?? job.JobNameInWorker}' has been auto-disabled after {job.AutoDisableSettings.ConsecutiveFailureCount} consecutive failures.",
                Severity = AlertSeverity.Warning,
                Source = nameof(StatusTrackerService),
                ThreadKey = $"job-auto-disabled-{job.Id}",
                ActionLink = $"/jobs/{job.Id}",
                AdditionalData = new
                {
                    JobId = job.Id,
                    JobName = job.DisplayName ?? job.JobNameInWorker,
                    FailureCount = job.AutoDisableSettings.ConsecutiveFailureCount,
                    job.AutoDisableSettings.DisableReason,
                    job.AutoDisableSettings.DisabledAt
                }
            });
        }
    }

    /// <summary>
    /// Sends alerts for failed job executions using fire-and-forget pattern.
    /// </summary>
    private void SendJobExecutionFailedAlerts(List<FailedOccurrenceInfo> failedOccurrences)
    {
        foreach (var failed in failedOccurrences)
        {
            _alertNotifier.SendFireAndForget(AlertType.JobExecutionFailed, new AlertPayload
            {
                Title = "Job Execution Failed",
                Message = $"Job '{failed.JobName}' failed. Error: {TruncateException(failed.Exception, 150)}",
                Severity = AlertSeverity.Error,
                Source = nameof(StatusTrackerService),
                ThreadKey = $"job-failed-{failed.JobId}",
                ActionLink = $"/jobs/{failed.JobId}",
                AdditionalData = new
                {
                    failed.JobId,
                    failed.JobName,
                    failed.CorrelationId,
                    Exception = TruncateException(failed.Exception, 500)
                }
            });
        }
    }

    /// <summary>
    /// Stops the background service and cleans up resources.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.Information("StatusTrackerService stopping...");

        // Cancels the consumer and batch loop and waits for them, so nothing else touches the batch below.
        await base.StopAsync(cancellationToken);

        try
        {
            //  Process remaining status updates
            await ProcessBatchAsync(CancellationToken.None);

            // Otherwise jobs stay flagged as running in Redis until the zombie detector's reconcile grace period ends.
            await DrainPendingRedisCompletionsAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to process remaining batch during shutdown");
        }

        // Close only channel (connection is managed by RabbitMQConnectionFactory)
        await _channel.SafeCloseAsync(_logger, cancellationToken);

        // Dispose semaphores last: the work above still needs them
        _batchLock?.Dispose();
        _channelLock?.Dispose();

        _logger.Information("StatusTrackerService stopped");
    }

    /// <summary>
    /// Updates consumer-level job counters in Redis (batch operation with Lua script).
    /// Groups updates by workerId/instanceId/jobType and applies net changes in SINGLE Redis call.
    /// Example: 100 updates with net change calculation → 1 Lua script call
    /// </summary>
    private async Task UpdateConsumerCountersBatchAsync(List<ConsumerCounterUpdate> updates, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _serviceProvider.CreateAsyncScope();
            var redisWorkerService = scope.ServiceProvider.GetService<IRedisWorkerService>();

            if (redisWorkerService == null)
                return;

            // Group by workerId:instanceId:jobType and calculate net change
            // Key format: "workerId:instanceId:jobType" for instance-level tracking
            var netChanges = new Dictionary<string, int>();

            foreach (var update in updates)
            {
                // Use workerId:instanceId:jobType for instance-level job tracking
                var key = $"{update.WorkerId}:{update.InstanceId}:{update.JobType}";

                // Increment when job starts running
                if (update.PreviousStatus != JobOccurrenceStatus.Running && update.NewStatus == JobOccurrenceStatus.Running)
                {
                    netChanges.TryGetValue(key, out var currentChange);
                    netChanges[key] = currentChange + 1;
                }
                // Decrement when job finishes
                else if (update.PreviousStatus == JobOccurrenceStatus.Running && update.NewStatus.IsFinalStatus())
                {
                    netChanges.TryGetValue(key, out var currentChange);
                    netChanges[key] = currentChange - 1;
                }
            }

            if (netChanges.Count == 0)
                return;

            await redisWorkerService.BatchUpdateConsumerJobCountsAsync(netChanges, cancellationToken);

            _logger.Debug("Batch updated {Count} consumer counters (from {TotalUpdates} status changes)", netChanges.Count, updates.Count);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to update consumer counters (non-critical)");
        }
    }

    #region Internal Records

    /// <summary>
    /// A received message waiting to be persisted.
    /// </summary>
    private readonly record struct BatchItem(JobStatusUpdateMessage Message, ulong DeliveryTag, IChannel Channel);

    /// <summary>
    /// Represents a circuit breaker update for a job.
    /// </summary>
    private readonly record struct CircuitBreakerUpdate(bool IsFailed, string Exception);

    /// <summary>
    /// Represents a failed occurrence for alert notification.
    /// </summary>
    private readonly record struct FailedOccurrenceInfo(Guid JobId, string JobName, Guid CorrelationId, string Exception);

    /// <summary>
    /// Represents a consumer counter update for Redis batch processing.
    /// </summary>
    private readonly record struct ConsumerCounterUpdate(
        string WorkerId,
        string InstanceId,
        string JobType,
        JobOccurrenceStatus PreviousStatus,
        JobOccurrenceStatus NewStatus);

    /// <summary>
    /// Result of applying status updates to occurrences.
    /// </summary>
    private sealed class StatusUpdateResult
    {
        public Dictionary<Guid, CircuitBreakerUpdate> CircuitBreakerUpdates { get; } = [];
        public List<ConsumerCounterUpdate> ConsumerCounterUpdates { get; } = [];
        public List<FailedOccurrenceInfo> FailedOccurrences { get; } = [];
    }

    #endregion
}