using EFCore.BulkExtensions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Features.ScheduledJobs.CreateScheduledJob;
using Milvaion.Application.Features.ScheduledJobs.UpdateScheduledJob;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Interfaces.Redis;
using Milvaion.Application.Utils.Constants;
using Milvaion.Infrastructure.BackgroundServices.Base;
using Milvaion.Infrastructure.Extensions;
using Milvaion.Infrastructure.Persistence.Context;
using Milvaion.Infrastructure.Services.RabbitMQ;
using Milvaion.Infrastructure.Telemetry;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Utils;
using Milvasoft.Types.Structs;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Milvaion.Infrastructure.BackgroundServices;

/// <summary>
/// Consumes external job registration and occurrence messages from RabbitMQ.
/// Handles job upsert from Quartz/Hangfire and creates/updates occurrences.
/// </summary>
public class ExternalJobTrackerService(IServiceProvider serviceProvider,
                                       RabbitMQConnectionFactory rabbitMQFactory,
                                       IOptions<ExternalJobTrackerOptions> options,
                                       IOptions<RabbitMQOptions> rabbitMQOptions,
                                       IRedisSchedulerService redisSchedulerService,
                                       IRedisStatsService redisStatsService,
                                       ILoggerFactory loggerFactory,
                                       BackgroundServiceMetrics metrics,
                                       IMemoryStatsRegistry memoryStatsRegistry = null) : MemoryTrackedBackgroundService(loggerFactory, options.Value, memoryStatsRegistry)
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly RabbitMQConnectionFactory _rabbitMQFactory = rabbitMQFactory;
    private readonly IRedisSchedulerService _redisSchedulerService = redisSchedulerService;
    private readonly IRedisStatsService _redisStatsService = redisStatsService;
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<ExternalJobTrackerService>();
    private readonly ExternalJobTrackerOptions _options = options.Value;
    private readonly RabbitMQOptions _rabbitMQOptions = rabbitMQOptions.Value;
    private readonly BackgroundServiceMetrics _metrics = metrics;

    private IChannel _registrationChannel;
    private IChannel _occurrenceChannel;

    // Batch queues
    private readonly ConcurrentQueue<ExternalJobRegistrationMessage> _registrationBatch = new();
    private readonly ConcurrentQueue<ExternalJobOccurrenceMessage> _occurrenceBatch = new();
    private readonly SemaphoreSlim _registrationBatchLock = new(1, 1);
    private readonly SemaphoreSlim _occurrenceBatchLock = new(1, 1);

    // Channel thread-safety: IChannel is NOT thread-safe, serialize ACK/NACK operations
    private readonly SemaphoreSlim _registrationChannelLock = new(1, 1);
    private readonly SemaphoreSlim _occurrenceChannelLock = new(1, 1);

    /// <inheritdoc/>
    protected override string ServiceName => "ExternalJobTracker";

    /// <inheritdoc />
    protected override async Task ExecuteWithMemoryTrackingAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.Warning("External job consumer is disabled. Skipping startup.");
            return;
        }

        _logger.Information("External job consumer is starting...");

        // Start batch processor tasks
        var registrationBatchTask = Task.Run(async () =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(_options.BatchIntervalMs, stoppingToken);
                await ProcessRegistrationBatchAsync(stoppingToken);
                TrackMemoryAfterIteration();
            }
        }, stoppingToken);

        var occurrenceBatchTask = Task.Run(async () =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(_options.BatchIntervalMs, stoppingToken);
                await ProcessOccurrenceBatchAsync(stoppingToken);
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
                retryCount = 0;
            }
            catch (OperationCanceledException)
            {
                _logger.Information("External job consumer is shutting down");
                break;
            }
            catch (Exception ex)
            {
                retryCount++;
                _logger.Error(ex, "ExternalJobTrackerService connection failed (attempt {Retry}/{MaxRetries})", retryCount, maxRetries);

                if (retryCount >= maxRetries)
                {
                    _logger.Fatal("ExternalJobTrackerService failed to connect after {MaxRetries} attempts.", maxRetries);
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds * retryCount), stoppingToken);
            }
        }
    }

    private async Task ConnectAndConsumeAsync(CancellationToken stoppingToken)
    {
        // Get channels from shared connection factory
        _registrationChannel = await _rabbitMQFactory.CreateChannelAsync(stoppingToken);
        _occurrenceChannel = await _rabbitMQFactory.CreateChannelAsync(stoppingToken);

        // Declare queues
        await _registrationChannel.QueueDeclareAsync(queue: WorkerConstant.Queues.ExternalJobRegistration,
                                                     durable: true,
                                                     exclusive: false,
                                                     autoDelete: false,
                                                     arguments: _rabbitMQOptions.BuildQueueArguments(),
                                                     cancellationToken: stoppingToken);

        await _occurrenceChannel.QueueDeclareAsync(queue: WorkerConstant.Queues.ExternalJobOccurrence,
                                                   durable: true,
                                                   exclusive: false,
                                                   autoDelete: false,
                                                   arguments: _rabbitMQOptions.BuildQueueArguments(),
                                                   cancellationToken: stoppingToken);

        // Set prefetch
        await _registrationChannel.BasicQosAsync(0, 10, false, stoppingToken);
        await _occurrenceChannel.BasicQosAsync(0, 20, false, stoppingToken);

        _logger.Information("ExternalJobTracker connected to RabbitMQ (shared connection). RegistrationChannel: {RegCh}, OccurrenceChannel: {OccCh}", _registrationChannel.ChannelNumber, _occurrenceChannel.ChannelNumber);

        // Setup registration consumer
        var registrationConsumer = new AsyncEventingBasicConsumer(_registrationChannel);
        registrationConsumer.ReceivedAsync += async (model, ea) =>
        {
            try
            {
                await ProcessRegistrationMessageAsync(ea, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error processing registration message");
            }
        };

        // Setup occurrence consumer
        var occurrenceConsumer = new AsyncEventingBasicConsumer(_occurrenceChannel);
        occurrenceConsumer.ReceivedAsync += async (model, ea) =>
        {
            try
            {
                await ProcessOccurrenceMessageAsync(ea, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error processing occurrence message");
            }
        };

        await _registrationChannel.BasicConsumeAsync(queue: WorkerConstant.Queues.ExternalJobRegistration,
                                                     autoAck: false,
                                                     consumer: registrationConsumer,
                                                     cancellationToken: stoppingToken);

        await _occurrenceChannel.BasicConsumeAsync(queue: WorkerConstant.Queues.ExternalJobOccurrence,
                                                   autoAck: false,
                                                   consumer: occurrenceConsumer,
                                                   cancellationToken: stoppingToken);

        _logger.Information("ExternalJobTracker consuming from {Queue1} and {Queue2}", WorkerConstant.Queues.ExternalJobRegistration, WorkerConstant.Queues.ExternalJobOccurrence);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task ProcessRegistrationMessageAsync(BasicDeliverEventArgs ea, CancellationToken cancellationToken)
    {
        try
        {
            var message = JsonSerializer.Deserialize<ExternalJobRegistrationMessage>(ea.Body.Span, ConstantJsonOptions.PropNameCaseInsensitive);

            if (message == null)
            {
                await _registrationChannel.SafeNackAsync(ea.DeliveryTag, _registrationChannelLock, _logger, cancellationToken);
                return;
            }

            _registrationBatch.Enqueue(message);

            if (_registrationBatch.Count >= _options.RegistrationBatchSize)
                await ProcessRegistrationBatchAsync(cancellationToken);

            await _registrationChannel.SafeAckAsync(ea.DeliveryTag, _registrationChannelLock, _logger, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to process registration message");

            await _registrationChannel.SafeNackAsync(ea.DeliveryTag, _registrationChannelLock, _logger, cancellationToken);
        }
    }

    private async Task ProcessOccurrenceMessageAsync(BasicDeliverEventArgs ea, CancellationToken cancellationToken)
    {
        try
        {
            var message = JsonSerializer.Deserialize<ExternalJobOccurrenceMessage>(ea.Body.Span, ConstantJsonOptions.PropNameCaseInsensitive);

            if (message == null)
            {
                await _occurrenceChannel.SafeNackAsync(ea.DeliveryTag, _occurrenceChannelLock, _logger, cancellationToken);
                return;
            }

            _occurrenceBatch.Enqueue(message);

            if (_occurrenceBatch.Count >= _options.OccurrenceBatchSize)
                await ProcessOccurrenceBatchAsync(cancellationToken);

            await _occurrenceChannel.SafeAckAsync(ea.DeliveryTag, _occurrenceChannelLock, _logger, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to process occurrence message");

            await _occurrenceChannel.SafeNackAsync(ea.DeliveryTag, _occurrenceChannelLock, _logger, cancellationToken);
        }
    }

    /// <summary>
    /// Process batch of job registration messages - upsert ScheduledJob records using MediatR handlers.
    /// </summary>
    private async Task ProcessRegistrationBatchAsync(CancellationToken cancellationToken)
    {
        if (_registrationBatch.IsEmpty)
            return;

        await _registrationBatchLock.WaitAsync(cancellationToken);

        try
        {
            if (_registrationBatch.IsEmpty)
                return;

            var batch = new List<ExternalJobRegistrationMessage>();

            while (_registrationBatch.TryDequeue(out var message))
                batch.Add(message);

            if (batch.Count == 0)
                return;

            await using var scope = _serviceProvider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

            // Deduplicate by ExternalJobId (last message wins)
            var jobsByExternalId = batch.GroupBy(m => m.ExternalJobId).ToDictionary(g => g.Key, g => g.Last());

            var externalIds = jobsByExternalId.Keys.ToList();

            // Find existing jobs by ExternalJobId field
            var existingJobs = await dbContext.ScheduledJobs.AsNoTracking().Where(j => j.IsExternal && externalIds.Contains(j.ExternalJobId)).ToListAsync(cancellationToken);

            var existingJobDict = existingJobs.ToDictionary(j => j.ExternalJobId, j => j);

            var createdCount = 0;
            var updatedCount = 0;

            // Collect new mappings to add to Redis
            var newMappings = new Dictionary<string, Guid>();

            foreach (var kvp in jobsByExternalId)
            {
                var externalId = kvp.Key;
                var message = kvp.Value;

                try
                {
                    if (existingJobDict.TryGetValue(externalId, out var existingJob))
                    {
                        // Update existing job via MediatR
                        var updateCommand = new UpdateScheduledJobCommand
                        {
                            Id = existingJob.Id,
                            DisplayName = new UpdateProperty<string>(message.DisplayName),
                            Description = new UpdateProperty<string>(message.Description),
                            CronExpression = new UpdateProperty<string>(message.CronExpression),
                            JobData = new UpdateProperty<string>(message.JobData),
                            IsActive = new UpdateProperty<bool>(message.IsActive),
                            InternalRequest = true,
                        };

                        var updateResult = await mediator.Send(updateCommand, cancellationToken);

                        if (updateResult.IsSuccess)
                        {
                            updatedCount++;

                            // Ensure Redis mapping exists for existing jobs too
                            newMappings[externalId] = existingJob.Id;
                        }
                        else
                            _logger.Warning("Failed to update external job {ExternalJobId}: {Message}", externalId, updateResult.Messages.FirstOrDefault()?.Message);
                    }
                    else
                    {
                        // Create new job via MediatR
                        var createCommand = new CreateScheduledJobCommand
                        {
                            DisplayName = message.DisplayName,
                            Description = message.Description ?? $"External job from {message.Source}",
                            Tags = BuildTags(message),
                            JobData = message.JobData ?? "{}",
                            ExecuteAt = message.NextExecuteAt ?? DateTime.UtcNow,
                            CronExpression = message.CronExpression,
                            IsActive = message.IsActive,
                            ConcurrentExecutionPolicy = ConcurrentExecutionPolicy.Queue, // External jobs manage their own concurrency
                            WorkerId = message.WorkerId,
                            SelectedJobName = message.ExternalJobId,
                            IsExternal = true,
                            ExternalJobId = message.ExternalJobId,
                            AutoDisableSettings = new Application.Dtos.ScheduledJobDtos.UpsertJobAutoDisableSettings
                            {
                                Enabled = false
                            },
                            InternalRequest = true
                        };

                        var createResult = await mediator.Send(createCommand, cancellationToken);

                        _logger.Information("External job {ExternalJobId} creation result: {IsSuccess}. Messages: {Messages}", externalId, createResult.IsSuccess, createResult.Messages.FirstOrDefault()?.Message);

                        if (createResult.IsSuccess)
                        {
                            createdCount++;

                            // Get the created job ID from DB and add to Redis mapping
                            var createdJob = await dbContext.ScheduledJobs.AsNoTracking()
                                                                          .Where(j => j.IsExternal && j.ExternalJobId == externalId)
                                                                          .Select(j => new { j.Id, j.ExternalJobId })
                                                                          .FirstOrDefaultAsync(cancellationToken);

                            if (createdJob != null)
                                newMappings[externalId] = createdJob.Id;
                        }
                        else
                            _logger.Warning("Failed to create external job {ExternalJobId}: {Message}", externalId, createResult.Messages.FirstOrDefault()?.Message);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error processing external job {ExternalJobId}", externalId);
                }
            }

            // Bulk add Redis mappings for ExternalJobId → JobId
            if (newMappings.Count > 0)
            {
                await _redisSchedulerService.SetExternalJobIdMappingsBulkAsync(newMappings, cancellationToken);

                _logger.Debug("Added {Count} external job ID mappings to Redis", newMappings.Count);
            }

            if (createdCount > 0)
                _logger.Information("Created {Count} external jobs via handlers", createdCount);

            if (updatedCount > 0)
                _logger.Information("Updated {Count} external jobs via handlers", updatedCount);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to process registration batch");
        }
        finally
        {
            _registrationBatchLock.Release();
        }
    }

    /// <summary>
    /// Process batch of occurrence messages - create/update JobOccurrence records.
    /// </summary>
    private async Task ProcessOccurrenceBatchAsync(CancellationToken cancellationToken)
    {
        if (_occurrenceBatch.IsEmpty)
            return;

        await _occurrenceBatchLock.WaitAsync(cancellationToken);

        try
        {
            if (_occurrenceBatch.IsEmpty)
                return;

            var batch = new List<ExternalJobOccurrenceMessage>();

            while (_occurrenceBatch.TryDequeue(out var message))
                batch.Add(message);

            if (batch.Count == 0)
                return;

            await using var scope = _serviceProvider.CreateAsyncScope();
            var signalRPublisher = scope.ServiceProvider.GetRequiredService<IJobOccurrenceEventPublisher>();
            var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

            // Separate starting events (inserts) from completed events (updates)
            var startingEvents = batch.Where(m => m.EventType == ExternalOccurrenceEventType.Starting).ToList();
            var completedEvents = batch.Where(m => m.EventType != ExternalOccurrenceEventType.Starting).ToList();

            // Process starting events - create new occurrences
            if (startingEvents.Count > 0)
            {
                // Get all ExternalJobId → JobId mappings
                var externalJobIds = startingEvents.Select(m => m.ExternalJobId).Distinct().ToList();
                var jobIdMap = await _redisSchedulerService.GetJobIdsByExternalIdsBulkAsync(externalJobIds, cancellationToken);

                // Fallback to DB for any missing mappings
                var missingExternalIds = externalJobIds.Except(jobIdMap.Keys).ToList();

                if (missingExternalIds.Count > 0)
                {
                    var dbMappings = await dbContext.ScheduledJobs.AsNoTracking()
                                                                  .Where(j => j.IsExternal && missingExternalIds.Contains(j.ExternalJobId))
                                                                  .Select(j => new { j.Id, j.ExternalJobId })
                                                                  .ToListAsync(cancellationToken);

                    foreach (var mapping in dbMappings)
                        jobIdMap[mapping.ExternalJobId] = mapping.Id;
                }

                var occurrencesToInsert = new List<JobOccurrence>();

                foreach (var message in startingEvents)
                {
                    if (!jobIdMap.TryGetValue(message.ExternalJobId, out var jobId))
                    {
                        _logger.Warning("Could not find job for external ID {ExternalJobId}", message.ExternalJobId);
                        continue;
                    }

                    var occurrence = new JobOccurrence
                    {
                        Id = message.CorrelationId,
                        CreationDate = DateTime.UtcNow,
                        JobVersion = 1,
                        JobId = jobId,
                        JobName = message.JobTypeName,
                        CorrelationId = message.CorrelationId,
                        WorkerId = message.WorkerId,
                        Status = JobOccurrenceStatus.Running,
                        StartTime = message.StartTime ?? message.ActualFireTime ?? DateTime.UtcNow,
                        ExternalJobId = message.ExternalOccurrenceId,
                        StatusChangeLogs =
                        [
                            new() {
                                From = JobOccurrenceStatus.Queued,
                                To = JobOccurrenceStatus.Running,
                                Timestamp = DateTime.UtcNow,
                            }
                        ],
                        CreatedAt = DateTime.UtcNow,
                        CreatorUserName = GlobalConstant.SystemUsername
                    };

                    occurrencesToInsert.Add(occurrence);
                }

                if (occurrencesToInsert.Count > 0)
                {
                    await dbContext.BulkInsertAsync(occurrencesToInsert, cancellationToken: cancellationToken);

                    _logger.Debug("Created {Count} external job occurrences", occurrencesToInsert.Count);

                    // Update Redis counters
                    await _redisStatsService.IncrementTotalOccurrencesAsync(occurrencesToInsert.Count, cancellationToken);
                    await _redisStatsService.IncrementStatusCounterAsync(JobOccurrenceStatus.Running, occurrencesToInsert.Count, cancellationToken);

                    // Track executions for EPS/EPM calculation
                    await _redisStatsService.TrackExecutionsAsync([.. occurrencesToInsert.Select(o => o.Id)], cancellationToken);

                    // Publish SignalR notification for created occurrences
                    await signalRPublisher.PublishOccurrenceCreatedAsync(occurrencesToInsert, _logger, cancellationToken);
                }
            }

            // Process completed events - update existing occurrences
            if (completedEvents.Count > 0)
            {
                var correlationIds = completedEvents.Select(m => m.CorrelationId).ToList();

                var existingOccurrences = await dbContext.JobOccurrences.Where(o => correlationIds.Contains(o.CorrelationId)).ToListAsync(cancellationToken);

                var occurrenceDict = existingOccurrences.ToDictionary(o => o.CorrelationId);
                var updatedOccurrences = new List<JobOccurrence>();
                var statusChanges = new List<(JobOccurrenceStatus OldStatus, JobOccurrenceStatus NewStatus)>();

                foreach (var message in completedEvents)
                {
                    if (!occurrenceDict.TryGetValue(message.CorrelationId, out var occurrence))
                    {
                        _logger.Debug("Occurrence not found for CorrelationId: {CorrelationId}", message.CorrelationId);
                        continue;
                    }

                    var oldStatus = occurrence.Status;
                    occurrence.Status = message.Status;
                    occurrence.EndTime = message.EndTime ?? DateTime.UtcNow;
                    occurrence.DurationMs = message.DurationMs;
                    occurrence.Result = message.Result;
                    occurrence.Exception = message.Exception;
                    occurrence.StatusChangeLogs.Add(new OccurrenceStatusChangeLog
                    {
                        From = oldStatus,
                        To = message.Status,
                        Timestamp = DateTime.UtcNow
                    });

                    updatedOccurrences.Add(occurrence);
                    statusChanges.Add((oldStatus, message.Status));
                }

                if (updatedOccurrences.Count > 0)
                {
                    await dbContext.BulkUpdateAsync(updatedOccurrences, new BulkConfig
                    {
                        PropertiesToInclude =
                        [
                            nameof(JobOccurrence.Status),
                            nameof(JobOccurrence.EndTime),
                            nameof(JobOccurrence.DurationMs),
                            nameof(JobOccurrence.Result),
                            nameof(JobOccurrence.Exception),
                            nameof(JobOccurrence.StatusChangeLogs)
                        ]
                    }, cancellationToken: cancellationToken);

                    _logger.Debug("Updated {Count} external job occurrences", updatedOccurrences.Count);

                    // Update Redis counters for status changes
                    foreach (var (oldStatus, newStatus) in statusChanges)
                    {
                        if (oldStatus != newStatus)
                            await _redisStatsService.UpdateStatusCountersAsync(oldStatus, newStatus, cancellationToken);
                    }

                    // Track durations for average calculation (only for completed/failed jobs with duration)
                    foreach (var occurrence in updatedOccurrences.Where(o => o.DurationMs.HasValue))
                    {
                        await _redisStatsService.TrackDurationAsync(occurrence.DurationMs.Value, cancellationToken);
                    }

                    // Publish SignalR notification for updated occurrences
                    await signalRPublisher.PublishOccurrenceUpdatedAsync(updatedOccurrences, _logger, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to process occurrence batch");
        }
        finally
        {
            _occurrenceBatchLock.Release();
        }
    }

    private static string BuildTags(ExternalJobRegistrationMessage message)
    {
        var tags = new List<string> { $"source:{message.Source}" };

        if (!string.IsNullOrEmpty(message.Tags))
            tags.Add(message.Tags);

        return string.Join(",", tags);
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.Information("ExternalJobTracker stopping...");

        // Cancel the stoppingToken and wait for ExecuteAsync to complete first,
        // so batch tasks and consumers are no longer running before cleanup.
        await base.StopAsync(cancellationToken);

        // Process remaining batches
        try
        {
            await ProcessRegistrationBatchAsync(CancellationToken.None);
            await ProcessOccurrenceBatchAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to process remaining batches during shutdown");
        }

        // Dispose semaphores
        _registrationBatchLock?.Dispose();
        _occurrenceBatchLock?.Dispose();
        _registrationChannelLock?.Dispose();
        _occurrenceChannelLock?.Dispose();

        // Close only channels (connection is managed by RabbitMQConnectionFactory)
        await _registrationChannel.SafeCloseAsync(_logger, cancellationToken);
        await _occurrenceChannel.SafeCloseAsync(_logger, cancellationToken);

        _logger.Information("ExternalJobTracker stopped");
    }
}
