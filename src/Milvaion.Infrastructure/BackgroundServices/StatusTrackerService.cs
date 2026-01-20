using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Interfaces.Redis;
using Milvaion.Application.Utils.Constants;
using Milvaion.Infrastructure.BackgroundServices.Base;
using Milvaion.Infrastructure.Persistence.Context;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Utils;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text.Json;

namespace Milvaion.Infrastructure.BackgroundServices;

/// <summary>
/// Consumes job status updates from RabbitMQ and updates JobOccurrence records.
/// </summary>
public class StatusTrackerService(IServiceProvider serviceProvider,
                                  IRedisSchedulerService redisScheduler,
                                  IOptions<RabbitMQOptions> rabbitOptions,
                                  IOptions<StatusTrackerOptions> options,
                                  IOptions<JobAutoDisableOptions> autoDisableOptions,
                                  ILoggerFactory loggerFactory,
                                  IMemoryStatsRegistry memoryStatsRegistry = null) : MemoryTrackedBackgroundService(loggerFactory, options.Value, memoryStatsRegistry)
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IRedisSchedulerService _redisScheduler = redisScheduler;
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<StatusTrackerService>();
    private readonly RabbitMQOptions _rabbitOptions = rabbitOptions.Value;
    private readonly StatusTrackerOptions _options = options.Value;
    private readonly JobAutoDisableOptions _autoDisableOptions = autoDisableOptions.Value;
    private IConnection _connection;
    private IChannel _channel;

    //  Batch processing
    private readonly System.Collections.Concurrent.ConcurrentQueue<JobStatusUpdateMessage> _statusBatch = new();
    private readonly SemaphoreSlim _batchLock = new(1, 1);
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
                await Task.Delay(_options.BatchIntervalMs, stoppingToken);

                await ProcessBatchAsync(stoppingToken);

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
                _logger.Information("Status tracking is shutting down");
                break;
            }
            catch (Exception ex)
            {
                retryCount++;

                _logger.Error(ex, "StatusTrackerService connection failed (attempt {Retry}/{MaxRetries})", retryCount, maxRetries);

                if (retryCount >= maxRetries)
                {
                    _logger.Fatal("StatusTrackerService failed to connect after {MaxRetries} attempts. Service will be disabled until application restart.", maxRetries);
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds * retryCount), stoppingToken);
            }
        }
    }

    private async Task ConnectAndConsumeAsync(CancellationToken stoppingToken)
    {
        // Setup RabbitMQ connection
        var factory = new ConnectionFactory
        {
            HostName = _rabbitOptions.Host,
            Port = _rabbitOptions.Port,
            UserName = _rabbitOptions.Username,
            Password = _rabbitOptions.Password,
            VirtualHost = _rabbitOptions.VirtualHost,
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(10),
            TopologyRecoveryEnabled = true
        };

        _connection = await factory.CreateConnectionAsync(stoppingToken);

        // Subscribe to connection events
        _connection.ConnectionShutdownAsync += async (sender, args) =>
        {
            _logger.Warning("StatusTracker RabbitMQ connection shutdown. Reason: {Reason}", args.ReplyText);
            await Task.CompletedTask;
        };

        _connection.RecoverySucceededAsync += async (sender, args) =>
        {
            _logger.Information("StatusTracker RabbitMQ connection recovered successfully");
            await Task.CompletedTask;
        };

        _connection.ConnectionRecoveryErrorAsync += async (sender, args) =>
        {
            _logger.Error(args.Exception, "StatusTracker RabbitMQ connection recovery failed");
            await Task.CompletedTask;
        };

        _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);

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
                                         arguments: null,
                                         cancellationToken: stoppingToken);

        // Set prefetch count
        await _channel.BasicQosAsync(0, 10, false, stoppingToken);

        _logger.Information("StatusTracker connected to RabbitMQ. Queue: {Queue}", WorkerConstant.Queues.StatusUpdates);

        // Setup consumer with all event handlers
        var consumer = new AsyncEventingBasicConsumer(_channel);

        consumer.ReceivedAsync += async (model, ea) =>
        {
            try
            {
                await ProcessStatusUpdateAsync(ea, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error in StatusTracker consumer handler for CorrelationId: {CorrelationId}", ea.BasicProperties?.CorrelationId);
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

        _logger.Information("StatusTrackerService consuming from {Queue}. ConsumerTag: {ConsumerTag}, ChannelOpen: {IsOpen}, ChannelNumber: {ChannelNumber}",
            WorkerConstant.Queues.StatusUpdates, consumerTag, _channel.IsOpen, _channel.ChannelNumber);

        // Keep running until cancellation
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.Information("StatusTracker consumer loop cancelled gracefully");
            throw;
        }
    }

    private async Task ProcessStatusUpdateAsync(BasicDeliverEventArgs ea, CancellationToken cancellationToken)
    {
        try
        {
            // Parse message
            var message = JsonSerializer.Deserialize<JobStatusUpdateMessage>(ea.Body.Span, ConstantJsonOptions.PropNameCaseInsensitive);

            if (message == null)
            {
                _logger.Debug("Failed to deserialize status update message");

                await _channel.BasicNackAsync(ea.DeliveryTag, false, false, cancellationToken);

                return;
            }

            //  ONLY mark as Running immediately (for dispatcher checks)
            if (message.Status == JobOccurrenceStatus.Running)
            {
                try
                {
                    await _redisScheduler.TryMarkJobAsRunningAsync(message.JobId, message.CorrelationId, cancellationToken);
                    _logger.Debug("Job {JobId} immediately marked as running in Redis (CorrelationId: {CorrelationId})", message.JobId, message.CorrelationId);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to immediately mark job {JobId} as running in Redis (non-critical)", message.JobId);
                }
            }

            //  Add to batch queue (NO DB operation!)
            _statusBatch.Enqueue(message);

            //  Trigger immediate batch if full
            if (_statusBatch.Count >= _options.BatchSize)
                await ProcessBatchAsync(cancellationToken);

            // ACK the message
            await _channel.BasicAckAsync(ea.DeliveryTag, false, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to process status update message");

            await _channel.BasicNackAsync(ea.DeliveryTag, false, false, cancellationToken);
        }
    }

    /// <summary>
    /// Process batch of status updates - single DB transaction.
    /// </summary>
    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        if (_statusBatch.IsEmpty)
            return;

        //  Use semaphore to ensure single access
        await _batchLock.WaitAsync(cancellationToken);

        try
        {
            if (_statusBatch.IsEmpty)
                return;

            var batch = new List<JobStatusUpdateMessage>();

            while (_statusBatch.TryDequeue(out var message))
            {
                batch.Add(message);
            }

            if (batch.Count == 0)
                return;

            try
            {
                await using var scope = _serviceProvider.CreateAsyncScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

                // Use Dictionary instead of GroupBy (last update wins)
                var statusByCorrelation = new Dictionary<Guid, JobStatusUpdateMessage>(batch.Count);

                // Last update wins - overwrite if already exists
                foreach (var message in batch)
                    statusByCorrelation[message.CorrelationId] = message;

                var correlationIds = statusByCorrelation.Keys.ToList();

                //  Single query for all occurrences
                var occurrences = await dbContext.JobOccurrences
                                                 .AsNoTracking()
                                                 .Where(o => correlationIds.Contains(o.CorrelationId))
                                                 .Select(JobOccurrence.Projections.UpdateStatus)
                                                 .ToListAsync(cancellationToken: cancellationToken);

                if (occurrences.Count == 0)
                {
                    _logger.Debug("No matching occurrences found for {Count} status updates", batch.Count);
                    return;
                }

                var occurrenceDict = occurrences.ToDictionary(o => o.CorrelationId);

                // Track jobs that need circuit breaker updates
                var jobsToUpdateCircuitBreaker = new Dictionary<Guid, (bool isFailed, string exception)>();

                //  Update in memory (NO foreach DB operation!)
                foreach (var kvp in statusByCorrelation)
                {
                    if (!occurrenceDict.TryGetValue(kvp.Key, out var occurrence))
                    {
                        _logger.Debug("JobOccurrence not found for CorrelationId: {CorrelationId}", kvp.Key);
                        continue;
                    }

                    var message = kvp.Value;

                    // Track status change history
                    var previousStatus = occurrence.Status;
                    var newStatus = message.Status;

                    // Detect if this is a heartbeat-only update (no status change, no other data)
                    var isHeartbeat = previousStatus == newStatus
                                      && newStatus == JobOccurrenceStatus.Running
                                      && message.EndTime == null
                                      && message.DurationMs == null
                                      && string.IsNullOrEmpty(message.Result);

                    if (isHeartbeat)
                    {
                        occurrence.LastHeartbeat = DateTime.UtcNow;

                        _logger.Debug("Heartbeat received for CorrelationId: {CorrelationId}", kvp.Key);

                        continue; // Skip other processing
                    }

                    occurrence.StatusChangeLogs ??= [];

                    // Limit status change logs to prevent unbounded growth
                    if (occurrence.StatusChangeLogs.Count >= 100)
                        occurrence.StatusChangeLogs = [.. occurrence.StatusChangeLogs.OrderByDescending(l => l.Timestamp).Take(100)];

                    if (previousStatus != newStatus)
                        occurrence.StatusChangeLogs.Add(new OccurrenceStatusChangeLog
                        {
                            Timestamp = DateTime.UtcNow,
                            From = previousStatus,
                            To = newStatus
                        });

                    // Update consumer-level counters in Redis when status changes
                    await UpdateConsumerCountersAsync(message.WorkerId, occurrence.JobName, previousStatus, newStatus, cancellationToken);

                    // Track for circuit breaker update
                    if (newStatus == JobOccurrenceStatus.Failed)
                    {
                        jobsToUpdateCircuitBreaker[occurrence.JobId] = (true, message.Exception ?? "Unknown error");
                    }
                    else if (newStatus == JobOccurrenceStatus.Completed)
                    {
                        jobsToUpdateCircuitBreaker[occurrence.JobId] = (false, null);
                    }

                    occurrence.Status = message.Status;
                    occurrence.WorkerId = message.WorkerId;

                    if (message.StartTime.HasValue)
                        occurrence.StartTime = message.StartTime;

                    if (message.EndTime.HasValue)
                        occurrence.EndTime = message.EndTime;

                    if (message.DurationMs.HasValue)
                        occurrence.DurationMs = message.DurationMs;

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

                }

                await dbContext.BulkUpdateAsync(occurrences, (bc) =>
                {
                    bc.PropertiesToInclude = bc.PropertiesToIncludeOnUpdate = _updatePropNames;
                }, cancellationToken: cancellationToken);

                // Process circuit breaker updates for jobs
                if (jobsToUpdateCircuitBreaker.Count > 0 && _autoDisableOptions.Enabled)
                    await ProcessCircuitBreakerUpdatesAsync(dbContext, jobsToUpdateCircuitBreaker, cancellationToken);

                // Batch Redis updates instead of one-by-one
                var redisUpdateTasks = new List<Task>(occurrences.Count);

                foreach (var occurrence in occurrences)
                {
                    // Mark as running when job actually starts (atomic check-and-set)
                    if (occurrence.Status == JobOccurrenceStatus.Running)
                    {
                        // Use TryMarkJobAsRunningAsync for atomic operation
                        // Fire and forget - don't block on Redis response
                        redisUpdateTasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                var marked = await _redisScheduler.TryMarkJobAsRunningAsync(occurrence.JobId, occurrence.CorrelationId, cancellationToken);

                                if (!marked)
                                {
                                    _logger.Debug("Job {JobId} already marked as running by another occurrence (CorrelationId: {CorrelationId})", occurrence.JobId, occurrence.CorrelationId);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.Debug(ex, "Failed to mark job {JobId} as running (non-critical)", occurrence.JobId);
                            }
                        }, cancellationToken));
                    }
                    // Mark as completed when job finishes (any final status)
                    else if (occurrence.Status.IsFinalStatus())
                    {
                        redisUpdateTasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                await _redisScheduler.MarkJobAsCompletedAsync(occurrence.JobId, cancellationToken);
                            }
                            catch (Exception ex)
                            {
                                _logger.Debug(ex, "Failed to mark job {JobId} as completed (non-critical)", occurrence.JobId);
                            }
                        }, cancellationToken));
                    }
                }

                // Execute all Redis updates in parallel with timeout
                if (redisUpdateTasks.Count > 0)
                {
                    var redisTimeout = Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                    var redisCompleted = await Task.WhenAny(Task.WhenAll(redisUpdateTasks), redisTimeout);

                    if (redisCompleted == redisTimeout)
                        _logger.Warning("Redis updates timed out after 3 seconds for {Count} occurrences", redisUpdateTasks.Count);
                }

                // Parallel SignalR event publishing with timeout
                var eventPublisher = scope.ServiceProvider.GetService<IJobOccurrenceEventPublisher>();

                await eventPublisher.PublishOccurrenceUpdatedAsync(occurrences, _logger, cancellationToken);

                _logger.Debug("Processed {Count} status updates in batch", batch.Count);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to process status update batch");
            }
        }
        finally
        {
            _batchLock.Release();
        }
    }

    /// <summary>
    /// Processes circuit breaker updates for jobs that completed or failed.
    /// Increments failure count on failure, resets on success, auto-disables if threshold reached.
    /// </summary>
    private async Task ProcessCircuitBreakerUpdatesAsync(MilvaionDbContext dbContext,
                                                         Dictionary<Guid, (bool isFailed, string exception)> jobUpdates,
                                                         CancellationToken cancellationToken)
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
            var failureWindowThreshold = DateTime.UtcNow.AddMinutes(-_autoDisableOptions.FailureWindowMinutes);

            foreach (var job in jobs)
            {
                var (isFailed, exception) = jobUpdates[job.Id];

                // Check if auto-disable is enabled for this job
                var isAutoDisableEnabled = job.AutoDisableSettings.Enabled ?? true; // Use job-specific or default to true

                if (!isAutoDisableEnabled)
                {
                    // Job explicitly disabled auto-disable, just track failures but don't disable
                    if (isFailed)
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

                if (isFailed)
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
                        job.AutoDisableSettings.DisableReason = $"Auto-disabled after {job.AutoDisableSettings.ConsecutiveFailureCount} consecutive failures. Last error: {TruncateException(exception, 200)}";

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

            // Remove auto-disabled jobs from Redis scheduler
            foreach (var job in autoDisabledJobs)
            {
                try
                {
                    await _redisScheduler.RemoveFromScheduledSetAsync(job.Id, cancellationToken);
                    await _redisScheduler.RemoveCachedJobAsync(job.Id, cancellationToken);
                    _logger.Debug("Removed auto-disabled job {JobId} from Redis scheduler", job.Id);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to remove auto-disabled job {JobId} from Redis (non-critical)", job.Id);
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
    /// Stops the background service and cleans up resources.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.Information("StatusTrackerService stopping...");

        //  Process remaining status updates
        await ProcessBatchAsync(cancellationToken);

        // Dispose semaphore
        _batchLock?.Dispose();

        if (_channel != null)
        {
            await _channel.CloseAsync(cancellationToken);
            _channel.Dispose();
        }

        if (_connection != null)
        {
            await _connection.CloseAsync(cancellationToken);
            _connection.Dispose();
        }

        await base.StopAsync(cancellationToken);
        _logger.Information("StatusTrackerService stopped");
    }

    /// <summary>
    /// Updates consumer-level job counters in Redis when job status changes.
    /// Increments when job starts (Queued -> Running), decrements when job ends (Running -> Final).
    /// </summary>
    private async Task UpdateConsumerCountersAsync(string workerId, string jobType, JobOccurrenceStatus previousStatus, JobOccurrenceStatus newStatus, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(workerId) || string.IsNullOrEmpty(jobType))
            return;

        try
        {
            // Get IRedisWorkerService from service provider
            await using var scope = _serviceProvider.CreateAsyncScope();
            var redisWorkerService = scope.ServiceProvider.GetService<IRedisWorkerService>();

            if (redisWorkerService == null)
                return;

            // Increment when job starts running
            if (previousStatus != JobOccurrenceStatus.Running && newStatus == JobOccurrenceStatus.Running)
            {
                await redisWorkerService.IncrementConsumerJobCountAsync(workerId, jobType, cancellationToken);
                _logger.Debug("Incremented consumer counter for {WorkerId}/{JobType}", workerId, jobType);
            }
            // Decrement when job finishes
            else if (previousStatus == JobOccurrenceStatus.Running && newStatus.IsFinalStatus())
            {
                await redisWorkerService.DecrementConsumerJobCountAsync(workerId, jobType, cancellationToken);
                _logger.Debug("Decremented consumer counter for {WorkerId}/{JobType}", workerId, jobType);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to update consumer counters for {WorkerId}/{JobType} (non-critical)", workerId, jobType);
        }
    }
}
