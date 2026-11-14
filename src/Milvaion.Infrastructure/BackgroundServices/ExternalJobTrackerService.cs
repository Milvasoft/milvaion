using Milvasoft.BulkExtensions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Features.ScheduledJobs.CreateScheduledJob;
using Milvaion.Application.Features.ScheduledJobs.UpdateScheduledJob;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Interfaces.Messaging;
using Milvaion.Application.Interfaces.Redis;
using Milvaion.Application.Utils.Constants;
using Milvaion.Infrastructure.BackgroundServices.Base;
using Milvaion.Infrastructure.Extensions;
using Milvaion.Infrastructure.Persistence.Context;
using Milvaion.Infrastructure.Telemetry;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Utils;
using Milvasoft.Types.Structs;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Milvaion.Infrastructure.BackgroundServices;

/// <summary>
/// Consumes external job registration and occurrence messages from the message transport.
/// Handles job upsert from Quartz/Hangfire and creates/updates occurrences.
/// </summary>
public class ExternalJobTrackerService(IServiceProvider serviceProvider,
                                       IMessageConsumerTransport transport,
                                       IOptions<ExternalJobTrackerOptions> options,
                                       IRedisSchedulerService redisSchedulerService,
                                       IRedisStatsService redisStatsService,
                                       ILoggerFactory loggerFactory,
                                       BackgroundServiceMetrics metrics,
                                       IMemoryStatsRegistry memoryStatsRegistry = null) : MemoryTrackedBackgroundService(loggerFactory, options.Value, memoryStatsRegistry)
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IMessageConsumerTransport _transport = transport;
    private readonly IRedisSchedulerService _redisSchedulerService = redisSchedulerService;
    private readonly IRedisStatsService _redisStatsService = redisStatsService;
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<ExternalJobTrackerService>();
    private readonly ExternalJobTrackerOptions _options = options.Value;
    private readonly BackgroundServiceMetrics _metrics = metrics;

    private IMessageConsumer _registrationConsumer;
    private IMessageConsumer _occurrenceConsumer;

    // Batch queues
    // The delivery travels with the message so it can be acknowledged once the batch is written.
    private readonly ConcurrentQueue<Received<ExternalJobRegistrationMessage>> _registrationBatch = new();
    private readonly ConcurrentQueue<Received<ExternalJobOccurrenceMessage>> _occurrenceBatch = new();

    // How long a completion may wait for its Starting event, which another instance may still be writing.
    private static readonly TimeSpan _orphanCompletionMaxWait = TimeSpan.FromSeconds(30);

    private readonly record struct Received<T>(T Message, IMessageDelivery Delivery, DateTime ReceivedAt);
    private readonly SemaphoreSlim _registrationBatchLock = new(1, 1);
    private readonly SemaphoreSlim _occurrenceBatchLock = new(1, 1);


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

                try
                {
                    await ProcessRegistrationBatchAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Error(ex, "Error in registration batch loop");
                }

                TrackMemoryAfterIteration();
            }
        }, stoppingToken);

        var occurrenceBatchTask = Task.Run(async () =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(_options.BatchIntervalMs, stoppingToken);

                try
                {
                    await ProcessOccurrenceBatchAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Error(ex, "Error in occurrence batch loop");
                }
            }
        }, stoppingToken);

        await RabbitMQChannelExtensions.RunConsumerLoopAsync(ConnectAndConsumeAsync, _logger, ServiceName, null, stoppingToken);
    }

    private async Task ConnectAndConsumeAsync(CancellationToken stoppingToken)
    {
        // Messages stay unacked until written, so the window has to cover at least a full batch.
        var registrationPrefetch = (ushort)Math.Clamp(_options.RegistrationBatchSize * 2, 10, ushort.MaxValue);
        var occurrencePrefetch = (ushort)Math.Clamp(_options.OccurrenceBatchSize * 2, 20, ushort.MaxValue);

        var registrationConsumer = await _transport.StartConsumerAsync(new ConsumerSubscription(WorkerConstant.Queues.ExternalJobRegistration, registrationPrefetch, ServiceName + ".Registration"),
                                                                       delivery => ProcessRegistrationMessageAsync(delivery, stoppingToken),
                                                                       stoppingToken);

        _registrationConsumer = registrationConsumer;

        IMessageConsumer occurrenceConsumer;

        try
        {
            occurrenceConsumer = await _transport.StartConsumerAsync(new ConsumerSubscription(WorkerConstant.Queues.ExternalJobOccurrence, occurrencePrefetch, ServiceName + ".Occurrence"),
                                                                     delivery => ProcessOccurrenceMessageAsync(delivery, stoppingToken),
                                                                     stoppingToken);
        }
        catch
        {
            await registrationConsumer.DisposeAsync();
            throw;
        }

        _occurrenceConsumer = occurrenceConsumer;

        _logger.Information("ExternalJobTracker consuming from {Queue1} and {Queue2}", WorkerConstant.Queues.ExternalJobRegistration, WorkerConstant.Queues.ExternalJobOccurrence);

        // Keep running until cancellation or until either consumer dies
        await Task.WhenAny(registrationConsumer.Completion, occurrenceConsumer.Completion).WaitAsync(stoppingToken);

        await registrationConsumer.DisposeAsync();
        await occurrenceConsumer.DisposeAsync();
    }

    private async Task ProcessRegistrationMessageAsync(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
        try
        {
            var message = JsonSerializer.Deserialize<ExternalJobRegistrationMessage>(delivery.Body.Span, ConstantJsonOptions.PropNameCaseInsensitive);

            if (message == null)
            {
                await delivery.NackAsync(requeue: false, cancellationToken);
                return;
            }

            // Acknowledged once the batch is written.
            _registrationBatch.Enqueue(new(message, delivery, DateTime.UtcNow));

            if (_registrationBatch.Count >= _options.RegistrationBatchSize)
                await ProcessRegistrationBatchAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to process registration message");

            await delivery.NackAsync(requeue: false, cancellationToken);
        }
    }

    private async Task ProcessOccurrenceMessageAsync(IMessageDelivery delivery, CancellationToken cancellationToken)
    {
        try
        {
            var message = JsonSerializer.Deserialize<ExternalJobOccurrenceMessage>(delivery.Body.Span, ConstantJsonOptions.PropNameCaseInsensitive);

            if (message == null)
            {
                await delivery.NackAsync(requeue: false, cancellationToken);
                return;
            }

            // Acknowledged once the batch is written.
            _occurrenceBatch.Enqueue(new(message, delivery, DateTime.UtcNow));

            if (_occurrenceBatch.Count >= _options.OccurrenceBatchSize)
                await ProcessOccurrenceBatchAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to process occurrence message");

            await delivery.NackAsync(requeue: false, cancellationToken);
        }
    }

    private static async Task AckAsync<T>(IEnumerable<Received<T>> items, CancellationToken cancellationToken)
    {
        foreach (var item in items)
            await item.Delivery.AckAsync(cancellationToken);
    }

    /// <summary>
    /// Process batch of job registration messages - upsert ScheduledJob records using MediatR handlers.
    /// </summary>
    private async Task ProcessRegistrationBatchAsync(CancellationToken cancellationToken)
    {
        if (_registrationBatch.IsEmpty)
            return;

        List<Received<ExternalJobRegistrationMessage>> received = null;

        await _registrationBatchLock.WaitAsync(cancellationToken);

        try
        {
            if (_registrationBatch.IsEmpty)
                return;

            received = [];

            while (_registrationBatch.TryDequeue(out var item))
                received.Add(item);

            if (received.Count == 0)
                return;

            var batch = received.Select(r => r.Message).ToList();

            await using var scope = _serviceProvider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var projectContext = scope.ServiceProvider.GetRequiredService<IProjectContext>();

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

                // External schedulers do not declare a project yet, so their jobs live in the Default project. The create and
                // update handlers are request handlers, so they run in that project's scope.
                using var projectScope = projectContext.EnterProjectScope(existingJobDict.TryGetValue(externalId, out var owner) ? owner.ProjectId : ProjectDefaults.DefaultProjectId);

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

            await AckAsync(received, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to process registration batch");

            foreach (var item in received ?? [])
                _registrationBatch.Enqueue(item);
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

        List<Received<ExternalJobOccurrenceMessage>> received = null;

        await _occurrenceBatchLock.WaitAsync(cancellationToken);

        try
        {
            if (_occurrenceBatch.IsEmpty)
                return;

            received = [];

            while (_occurrenceBatch.TryDequeue(out var item))
                received.Add(item);

            if (received.Count == 0)
                return;

            try
            {
                await PersistOccurrenceBatchAsync(received, cancellationToken);
            }
            catch (Exception ex) when (RabbitMQChannelExtensions.IsPermanentDataError(ex))
            {
                _logger.Error(ex, "External occurrence batch contains data the database rejects. Isolating the offending message(s).");

                foreach (var item in received)
                {
                    try
                    {
                        await PersistOccurrenceBatchAsync([item], cancellationToken);
                    }
                    catch (Exception itemEx) when (RabbitMQChannelExtensions.IsPermanentDataError(itemEx))
                    {
                        _logger.Error(itemEx, "Dropping external occurrence message the database rejects. CorrelationId: {CorrelationId}", item.Message.CorrelationId);

                        await item.Delivery.NackAsync(requeue: false, cancellationToken);
                    }
                    catch (Exception itemEx)
                    {
                        _logger.Warning(itemEx, "External occurrence {CorrelationId} will be retried in the next batch", item.Message.CorrelationId);

                        _occurrenceBatch.Enqueue(item);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to process occurrence batch");

            foreach (var item in received ?? [])
                _occurrenceBatch.Enqueue(item);
        }
        finally
        {
            _occurrenceBatchLock.Release();
        }
    }

    /// <summary>
    /// Creates and updates the occurrences of a batch. Safe to run again for messages that were redelivered.
    /// </summary>
    private async Task PersistOccurrenceBatchAsync(List<Received<ExternalJobOccurrenceMessage>> received, CancellationToken cancellationToken)
    {
        var settled = new List<Received<ExternalJobOccurrenceMessage>>(received.Count);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var signalRPublisher = scope.ServiceProvider.GetRequiredService<IJobOccurrenceEventPublisher>();
        var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

        // Separate starting events (inserts) from completed events (updates)
        var startingEvents = received.Where(r => r.Message.EventType == ExternalOccurrenceEventType.Starting).ToList();
        var completedEvents = received.Where(r => r.Message.EventType != ExternalOccurrenceEventType.Starting).ToList();

        // Process starting events - create new occurrences
        if (startingEvents.Count > 0)
        {
            // Get all ExternalJobId → JobId mappings
            var externalJobIds = startingEvents.Select(m => m.Message.ExternalJobId).Distinct().ToList();
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

            // A redelivered Starting must not hit the primary key and take the rest of the batch down with it.
            var startingIds = startingEvents.Select(r => r.Message.CorrelationId).ToList();
            var existingIds = (await dbContext.JobOccurrences.AsNoTracking().Where(o => startingIds.Contains(o.Id)).Select(o => o.Id).ToListAsync(cancellationToken)).ToHashSet();

            var occurrencesToInsert = new List<JobOccurrence>();

            foreach (var item in startingEvents)
            {
                var message = item.Message;

                settled.Add(item);

                if (!existingIds.Add(message.CorrelationId))
                    continue;

                if (!jobIdMap.TryGetValue(message.ExternalJobId, out var jobId))
                {
                    _logger.Warning("Could not find job for external ID {ExternalJobId}", message.ExternalJobId);
                    continue;
                }

                occurrencesToInsert.Add(new JobOccurrence
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
                });
            }

            if (occurrencesToInsert.Count > 0)
            {
                // Bulk inserts bypass SaveChanges, so the project is copied from the job here.
                var jobIds = occurrencesToInsert.Select(o => o.JobId).Distinct().ToList();
                var jobProjects = await dbContext.ScheduledJobs.AsNoTracking()
                                                               .Where(j => jobIds.Contains(j.Id))
                                                               .Select(j => new { j.Id, j.ProjectId })
                                                               .ToDictionaryAsync(j => j.Id, j => j.ProjectId, cancellationToken);

                foreach (var occurrence in occurrencesToInsert)
                    occurrence.ProjectId = jobProjects.GetValueOrDefault(occurrence.JobId, ProjectDefaults.DefaultProjectId);

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
            var correlationIds = completedEvents.Select(m => m.Message.CorrelationId).ToList();

            var existingOccurrences = await dbContext.JobOccurrences.Where(o => correlationIds.Contains(o.CorrelationId)).ToListAsync(cancellationToken);

            var occurrenceDict = existingOccurrences.ToDictionary(o => o.CorrelationId);
            var updatedOccurrences = new List<JobOccurrence>();
            var statusChanges = new List<(JobOccurrenceStatus OldStatus, JobOccurrenceStatus NewStatus)>();

            foreach (var item in completedEvents)
            {
                var message = item.Message;

                if (!occurrenceDict.TryGetValue(message.CorrelationId, out var occurrence))
                {
                    // Competing consumers can process Completed before Starting. Hold it back for a while.
                    if (DateTime.UtcNow - item.ReceivedAt < _orphanCompletionMaxWait)
                    {
                        _occurrenceBatch.Enqueue(item);
                        continue;
                    }

                    _logger.Warning("Occurrence not found for CorrelationId: {CorrelationId}. Dropping its completion.", message.CorrelationId);
                    settled.Add(item);
                    continue;
                }

                settled.Add(item);

                // A redelivered completion: already applied.
                if (occurrence.Status == message.Status)
                    continue;

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

        await AckAsync(settled, cancellationToken);
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

        // Stop the consumers only now: the batches above still had to be acknowledged on them
        if (_registrationConsumer != null)
            await _registrationConsumer.DisposeAsync();

        if (_occurrenceConsumer != null)
            await _occurrenceConsumer.DisposeAsync();

        // Dispose semaphores last: the work above still needs them
        _registrationBatchLock?.Dispose();
        _occurrenceBatchLock?.Dispose();

        _logger.Information("ExternalJobTracker stopped");
    }
}
