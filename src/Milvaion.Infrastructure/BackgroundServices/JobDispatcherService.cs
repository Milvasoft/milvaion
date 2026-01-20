using Cronos;
using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Interfaces.RabbitMQ;
using Milvaion.Application.Interfaces.Redis;
using Milvaion.Application.Utils.Constants;
using Milvaion.Infrastructure.BackgroundServices.Base;
using Milvaion.Infrastructure.Persistence.Context;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Utils;
using System.Collections.Concurrent;

namespace Milvaion.Infrastructure.BackgroundServices;

/// <summary>
/// Background service that dispatches scheduled jobs from Redis to RabbitMQ.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="JobDispatcherService"/> class.
/// </remarks>
public class JobDispatcherService(IServiceProvider serviceProvider,
                                  IRedisSchedulerService redisScheduler,
                                  IRedisLockService redisLock,
                                  IRedisWorkerService redisWorkerService,
                                  IRabbitMQPublisher rabbitMQPublisher,
                                  IOptions<JobDispatcherOptions> options,
                                  IDispatcherControlService controlService,
                                  ILoggerFactory loggerFactory,
                                  IMemoryStatsRegistry memoryStatsRegistry = null) : MemoryTrackedBackgroundService(loggerFactory, options.Value, memoryStatsRegistry)
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IRedisSchedulerService _redisScheduler = redisScheduler;
    private readonly IRedisLockService _redisLock = redisLock;
    private readonly IRedisWorkerService _redisWorkerService = redisWorkerService;
    private readonly IRabbitMQPublisher _rabbitMQPublisher = rabbitMQPublisher;
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<JobDispatcherService>();
    private readonly JobDispatcherOptions _options = options.Value;
    private readonly IDispatcherControlService _controlService = controlService;
    private readonly static List<string> _updatePropNames =
    [
        nameof(JobOccurrence.Status),
        nameof(JobOccurrence.Logs),
        nameof(JobOccurrence.Exception),
        nameof(JobOccurrence.DispatchRetryCount),
        nameof(JobOccurrence.NextDispatchRetryAt)
    ];

    /// <inheritdoc/>
    protected override string ServiceName => "JobDispatcher";

    // Compiled queries for non-Contains queries only
    private static readonly Func<MilvaionDbContext, DateTime, int, IAsyncEnumerable<JobOccurrence>> _getRetryOccurrencesCompiled =
        EF.CompileAsyncQuery((MilvaionDbContext context, DateTime now, int maxRetries) =>
            context.JobOccurrences
                   .AsNoTracking()
                   .Where(o => o.Status == JobOccurrenceStatus.Queued
                            && o.NextDispatchRetryAt != null
                            && o.NextDispatchRetryAt <= now
                            && o.DispatchRetryCount < maxRetries)
                   .Select(JobOccurrence.Projections.RetryFailed)
                   .Take(100));

    private static readonly Func<MilvaionDbContext, IAsyncEnumerable<ScheduledJob>> _getAllActiveJobsCompiled =
        EF.CompileAsyncQuery((MilvaionDbContext context) =>
            context.ScheduledJobs
                   .AsNoTracking()
                   .Where(j => j.IsActive)
                   .Select(ScheduledJob.Projections.CacheJob));

    /// <inheritdoc/>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.Warning("Job dispatcher is disabled. Skipping startup.");
            return;
        }

        _logger.Information("Job dispatcher service starting...");

        // Wait for database migrations to complete
        await WaitForDatabaseReadyAsync(cancellationToken);

        // Perform startup recovery (zombie detection)
        if (_options.EnableStartupRecovery)
            await PerformStartupRecoveryAsync(cancellationToken);

        await base.StartAsync(cancellationToken);

        _logger.Information("Job dispatcher service started successfully.");
    }

    /// <inheritdoc/>
    protected override async Task ExecuteWithMemoryTrackingAsync(CancellationToken stoppingToken)
    {
        _logger.Information("Job dispatcher polling started. Interval: {Interval}s, Batch Size: {BatchSize}", _options.PollingIntervalSeconds, _options.BatchSize);

        var consecutiveFailures = 0;
        const int maxConsecutiveFailures = 5;
        const int backoffSeconds = 30;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Check if dispatcher is enabled (runtime control)
                if (!_controlService.IsEnabled)
                {
                    _logger.Debug("Dispatcher paused by emergency stop. Waiting for resume signal...");
                    _isRunning = false;

                    // Check every 5 seconds
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

                    continue;
                }
                else
                    _isRunning = true;

                await DispatchDueJobsAsync(stoppingToken);

                TrackMemoryAfterIteration();

                // Reset failure counter on success
                if (consecutiveFailures > 0)
                {
                    _logger.Information("Job dispatcher recovered after {Failures} failures", consecutiveFailures);

                    consecutiveFailures = 0;
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
                _logger.Information("Job dispatcher shutting down gracefully");

                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;

                _logger.Error(ex, "Error during job dispatching iteration ({Failures}/{MaxFailures})", consecutiveFailures, maxConsecutiveFailures);

                // Circuit breaker: If too many failures, enter degraded mode
                if (consecutiveFailures >= maxConsecutiveFailures)
                {
                    _logger.Fatal("Job dispatcher entering degraded mode after {Failures} consecutive failures. Backing off for {Backoff}s before retry.", consecutiveFailures, backoffSeconds);

                    await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), stoppingToken);

                    // Reset after backoff
                    consecutiveFailures = 0;
                }
            }

            // Wait before next poll
            await Task.Delay(TimeSpan.FromSeconds(_options.PollingIntervalSeconds), stoppingToken);
        }

        _logger.Information("Job dispatcher polling stopped.");
    }

    /// <inheritdoc/>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.Information("Job dispatcher service stopping...");

        await base.StopAsync(cancellationToken);

        _logger.Information("Job dispatcher service stopped.");
    }

    /// <summary>
    /// Dispatches due jobs from Redis to RabbitMQ.
    /// </summary>
    private async Task DispatchDueJobsAsync(CancellationToken cancellationToken)
    {
        // 1. Get due jobs from Redis ZSET (with circuit breaker protection)
        List<Guid> dueJobIds;

        try
        {
            dueJobIds = await _redisScheduler.GetDueJobsAsync(DateTime.UtcNow, _options.BatchSize, cancellationToken);

            if (dueJobIds.Count == 0)
                return;
        }
        catch (Exception ex) when (ex.Message.Contains("Circuit breaker") || ex.Message.Contains("Redis"))
        {
            _logger.Error(ex, "Redis unavailable - circuit breaker may be open. Skipping this dispatch iteration.");

            // System continues with degraded functionality. Jobs will be dispatched in next iteration when Redis recovers
            return;
        }

        _logger.Debug("Found {Count} due jobs in Redis", dueJobIds.Count);

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

        var allJobs = await GetDueJobsAsync(dbContext, dueJobIds, cancellationToken);

        // 6. Check for running jobs from Redis (for ConcurrentExecutionPolicy)
        var jobIdsToCheck = allJobs.Keys.ToList();

        var runningJobIdsSet = await _redisScheduler.GetRunningJobIdsAsync(jobIdsToCheck, cancellationToken);

        // This prevents FK constraint violations
        await using var scope2 = _serviceProvider.CreateAsyncScope();
        var jobOccurrenceRepository = scope2.ServiceProvider.GetRequiredService<IMilvaionRepositoryBase<JobOccurrence>>();

        List<JobOccurrence> occurrences = [];
        List<JobOccurrence> occurrencesAsEvents = [];
        List<ScheduledJob> jobsToDispatch = [];
        List<Guid> toRemovedIdListScheduledSet = [];

        foreach (var jobId in dueJobIds)
        {
            // Validate job existence and active status
            if (!allJobs.TryGetValue(jobId, out var job) || !job.IsActive)
            {
                toRemovedIdListScheduledSet.Add(jobId);
                continue;
            }

            if (!await CanDispatchBasedOnConcurrencyPolicyAsync(runningJobIdsSet, job, cancellationToken))
                continue;

            if (!await CanDispatchBasedOnWorkerCapacityAsync(job, cancellationToken))
                continue;

            // All checks passed - create occurrence and add to dispatch list
            var correlationId = Guid.CreateVersion7();

            var occurrence = new JobOccurrence
            {
                Id = correlationId,
                CorrelationId = correlationId,
                JobId = job.Id,
                JobName = job.JobNameInWorker,
                JobVersion = job.Version, // Capture current job version
                ZombieTimeoutMinutes = job.ZombieTimeoutMinutes,
                ExecutionTimeoutSeconds = job.ExecutionTimeoutSeconds,
                WorkerId = job.WorkerId,
                Status = JobOccurrenceStatus.Queued,
                CreatedAt = DateTime.UtcNow,
                Logs =
                [
                    new()
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Information",
                        Message = "Job dispatched to RabbitMQ queue and will start closely...",
                        Category = "Dispatcher",
                        Data = new Dictionary<string, object>
                        {
                            ["ExecuteAt"] = job.ExecuteAt.ToString("O"),
                            ["WorkerId"] = job.WorkerId,
                            ["JobVersion"] = job.Version
                        }
                    }
                ],
                CreatorUserName = GlobalConstant.SystemUsername
            };

            occurrences.Add(occurrence);
            jobsToDispatch.Add(job);
            occurrencesAsEvents.Add(new JobOccurrence
            {
                Id = correlationId,
                JobId = job.Id,
                JobName = job.DisplayName,
                Status = JobOccurrenceStatus.Queued,
                WorkerId = job.WorkerId,
                CreatedAt = DateTime.UtcNow,
            });
        }

        if (toRemovedIdListScheduledSet.Count > 0)
        {
            _logger.Debug("Removed {Count} invalid jobs from Redis scheduled set", toRemovedIdListScheduledSet.Count);

            await _redisScheduler.RemoveFromScheduledSetBulkAsync(toRemovedIdListScheduledSet, cancellationToken);
        }

        if (occurrences.Count == 0)
        {
            _logger.Debug("No jobs to dispatch after pre-checks");

            return;
        }

        _logger.Debug("Created {Count} job occurrences, dispatching to RabbitMQ", occurrences.Count);

        try
        {
            await jobOccurrenceRepository.BulkAddAsync(occurrences, cancellationToken: cancellationToken);

            var eventPublisher = scope2.ServiceProvider.GetService<IJobOccurrenceEventPublisher>();

            await eventPublisher.PublishOccurrenceCreatedAsync(occurrencesAsEvents, _logger, cancellationToken);
        }
        catch (DbUpdateException)
        {
            #region Remove not exists in db but exists in redis

            // Get JobIds from merged collection
            var jobIdsToVerify = allJobs.Keys.ToList();

            // Normal query - Contains() works better without compiled query
            var existingJobIds = await dbContext.ScheduledJobs
                                                .AsNoTracking()
                                                .Where(j => jobIdsToVerify.Contains(j.Id))
                                                .Select(j => j.Id)
                                                .ToListAsync(cancellationToken);

            var existingJobIdsSet = existingJobIds.ToHashSet();

            // Remove jobs that don't exist in DB from our collection
            var invalidJobIds = jobIdsToVerify.Except(existingJobIdsSet).ToList();

            if (invalidJobIds.Count > 0)
            {
                _logger.Error("Found {Count} jobs in cache/Redis that don't exist in DB (FK violation risk), removing: {JobIds}", invalidJobIds.Count, string.Join(", ", invalidJobIds));

                foreach (var invalidId in invalidJobIds)
                {
                    allJobs.Remove(invalidId);

                    await _redisScheduler.RemoveFromScheduledSetAsync(invalidId, cancellationToken);
                    await _redisScheduler.RemoveCachedJobAsync(invalidId, cancellationToken);
                }

                occurrences.RemoveAll(j => invalidJobIds.Contains(j.JobId));

                await jobOccurrenceRepository.BulkAddAsync(occurrences, cancellationToken: cancellationToken);
            }

            #endregion
        }
        catch (Exception)
        {
            throw;
        }

        #region Dispatch To Rabbit

        // Track failed occurrences for bulk update
        var failedToDispatchOccurrences = new ConcurrentBag<JobOccurrence>();
        var occurrenceByJobId = occurrences.ToDictionary(o => o.JobId);

        // Dispatch to RabbitMQ
        await Parallel.ForEachAsync(jobsToDispatch, new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = cancellationToken
        }, async (job, ct) =>
        {
            var occurrence = occurrenceByJobId[job.Id];

            try
            {
                var success = await DispatchJobToRabbitMQAsync(job, occurrence, ct);

                if (!success)
                    failedToDispatchOccurrences.Add(occurrence);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Cancellation
                throw;
            }
            catch (Exception ex)
            {
                occurrence.Status = JobOccurrenceStatus.Failed;
                occurrence.Exception = $"Dispatch exception: {ex.Message}";
                occurrence.Logs.Add(new OccurrenceLog
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Error",
                    Message = $"Exception during dispatch: {ex.Message}",
                    Category = "Dispatcher"
                });

                failedToDispatchOccurrences.Add(occurrence);
            }
        });

        #region Arrange failed to dispatched occurrences

        // Track occurrences for retry with exponential backoff
        // Remove failed one-time jobs from Redis scheduled set
        // Recurring jobs will be rescheduled by HandleRecurringJobAsync
        if (!failedToDispatchOccurrences.IsEmpty)
        {
            foreach (var failedOccurrence in failedToDispatchOccurrences)
            {
                // Calculate next retry time with exponential backoff
                // Retry strategy: 30s, 1m, 2m, 4m, 8m (max 2 minutes total)
                var retryDelaySeconds = Math.Min(30 * Math.Pow(2, failedOccurrence.DispatchRetryCount), 120);
                var nextRetryTime = DateTime.UtcNow.AddSeconds(retryDelaySeconds);

                failedOccurrence.NextDispatchRetryAt = nextRetryTime;
                failedOccurrence.DispatchRetryCount++;

                _logger.Debug("Occurrence {OccurrenceId} failed to dispatch (attempt {Attempt}). Next retry at {RetryTime} (in {Delay}s)", failedOccurrence.Id, failedOccurrence.DispatchRetryCount, nextRetryTime, retryDelaySeconds);

                failedOccurrence.Logs.Add(new OccurrenceLog
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Warning",
                    Message = $"Dispatch failed (attempt {failedOccurrence.DispatchRetryCount}). Scheduled for retry at {nextRetryTime:HH:mm:ss}",
                    Category = "Dispatcher",
                    Data = new Dictionary<string, object>
                    {
                        ["RetryCount"] = failedOccurrence.DispatchRetryCount,
                        ["NextRetryAt"] = nextRetryTime.ToString("O"),
                        ["RetryDelaySeconds"] = retryDelaySeconds
                    }
                });

                var failedJob = jobsToDispatch.FirstOrDefault(j => j.Id == failedOccurrence.JobId);

                if (failedJob != null && string.IsNullOrWhiteSpace(failedJob.CronExpression))
                {
                    // One-time job failed - remove from Redis to prevent zombie
                    try
                    {
                        await _redisScheduler.RemoveFromScheduledSetAsync(failedJob.Id, cancellationToken);

                        _logger.Debug("Removed failed one-time job {JobId} from Redis scheduled set", failedJob.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Failed to remove job {JobId} from Redis after dispatch failure", failedJob.Id);
                    }
                }
            }

            await jobOccurrenceRepository.BulkUpdateAsync([.. failedToDispatchOccurrences], (bc) =>
            {
                bc.PropertiesToInclude = bc.PropertiesToIncludeOnUpdate = _updatePropNames;
            }, cancellationToken: cancellationToken);

            _logger.Debug("Updated {Count} failed occurrences with retry schedule", failedToDispatchOccurrences.Count);
        }

        // Retry failed dispatch occurrences (exponential backoff)
        await RetryFailedDispatchesAsync(cancellationToken);

        #endregion

        #endregion
    }

    private async Task<bool> CanDispatchBasedOnConcurrencyPolicyAsync(HashSet<Guid> runningJobIdsSet, ScheduledJob job, CancellationToken cancellationToken)
    {
        // Check ConcurrentExecutionPolicy
        var isRunning = runningJobIdsSet.Contains(job.Id);

        if (isRunning && job.ConcurrentExecutionPolicy == ConcurrentExecutionPolicy.Skip)
        {
            _logger.Information("Job {JobId} ({JobType}) skipped: Already running (Policy: Skip, Running in Redis: {IsRunning})", job.Id, job.JobNameInWorker, isRunning);

            // When job finishes, it will naturally become "due" again based on cron schedule
            return false;
        }

        // If policy is Skip but job NOT running, check if there are QUEUED messages in RabbitMQ
        if (!isRunning && job.ConcurrentExecutionPolicy == ConcurrentExecutionPolicy.Skip)
        {
            // Check job-specific queue using routing patterns
            var queuedCount = await _rabbitMQPublisher.GetQueueMessageCountAsync(job.RoutingPattern, cancellationToken);

            if (queuedCount > 0)
            {
                _logger.Information("Job {JobId} ({JobType}) has {Count} queued messages in RabbitMQ queue. Skipping new dispatch until queue drains (Policy: Skip)", job.Id, job.JobNameInWorker, queuedCount);

                // Job will be picked up again in next poll cycle
                // If queue still not empty, same check will apply
                return false;
            }
        }

        return true;
    }

    private async Task<bool> CanDispatchBasedOnWorkerCapacityAsync(ScheduledJob job, CancellationToken cancellationToken)
    {
        // PRE-CHECK: Worker availability and capacity BEFORE creating occurrence
        if (!string.IsNullOrWhiteSpace(job.WorkerId))
        {
            var isActive = await _redisWorkerService.IsWorkerActiveAsync(job.WorkerId, cancellationToken);

            if (!isActive)
            {
                _logger.Warning("Job {JobId} assigned to non-existent or inactive worker {WorkerId}. Will reschedule for next check (no occurrence created).", job.Id, job.WorkerId);

                // Reschedule for retry (no occurrence created)
                if (!string.IsNullOrWhiteSpace(job.CronExpression))
                {
                    await HandleRecurringJobAsync(job, cancellationToken);
                }
                else
                {
                    _logger.Information("One-time job {JobId} waiting for worker {WorkerId} to become available", job.Id, job.WorkerId);
                }

                return false;
            }

            // Check worker-level capacity (total)
            var (currentJobs, maxParallelJobs) = await _redisWorkerService.GetWorkerCapacityAsync(job.WorkerId, cancellationToken);

            if (maxParallelJobs.HasValue && currentJobs >= maxParallelJobs.Value)
            {
                _logger.Information("Worker {WorkerId} at capacity ({CurrentJobs}/{MaxJobs}), job {JobId} will retry later (no occurrence created)",
                                    job.WorkerId,
                                    currentJobs,
                                    maxParallelJobs.Value,
                                    job.Id);

                // Reschedule for retry (no occurrence created)
                if (!string.IsNullOrWhiteSpace(job.CronExpression))
                    await HandleRecurringJobAsync(job, cancellationToken);

                return false;
            }

            // Check consumer-level capacity (per job type)
            var (consumerCurrentJobs, consumerMaxParallel) = await _redisWorkerService.GetConsumerCapacityAsync(job.WorkerId, job.JobNameInWorker, cancellationToken);

            if (consumerMaxParallel.HasValue && consumerCurrentJobs >= consumerMaxParallel.Value)
            {
                _logger.Information("Consumer {WorkerId}/{JobType} at capacity ({CurrentJobs}/{MaxJobs}), job {JobId} will retry later (no occurrence created)",
                                    job.WorkerId,
                                    job.JobNameInWorker,
                                    consumerCurrentJobs,
                                    consumerMaxParallel.Value,
                                    job.Id);

                // Reschedule for retry (no occurrence created)
                if (!string.IsNullOrWhiteSpace(job.CronExpression))
                    await HandleRecurringJobAsync(job, cancellationToken);

                return false;
            }

            _logger.Debug("Worker {WorkerId} capacity check passed ({CurrentJobs}/{MaxJobs})", job.WorkerId, currentJobs, maxParallelJobs?.ToString() ?? "unlimited");
            _logger.Debug("Consumer {WorkerId}/{JobType} capacity check passed ({CurrentJobs}/{MaxJobs})", job.WorkerId, job.JobNameInWorker, consumerCurrentJobs, consumerMaxParallel?.ToString() ?? "unlimited");
        }

        return true;
    }

    /// <summary>
    /// Gets due jobs from Redis or database.
    /// </summary>
    /// <param name="dbContext"></param>
    /// <param name="dueJobIds"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task<Dictionary<Guid, ScheduledJob>> GetDueJobsAsync(MilvaionDbContext dbContext, List<Guid> dueJobIds, CancellationToken cancellationToken)
    {
        // 2. Get jobs from Redis cache
        var cachedJobs = await _redisScheduler.GetCachedJobsBulkAsync(dueJobIds, cancellationToken);

        _logger.Debug("Cache hit: {CacheHits}/{Total} jobs ({HitRate:P1})", cachedJobs.Count, dueJobIds.Count, cachedJobs.Count / (double)dueJobIds.Count);

        // 3. Find cache misses
        var cacheMissIds = dueJobIds.Except(cachedJobs.Keys).ToList();

        // 4. Fetch cache misses from database
        Dictionary<Guid, ScheduledJob> dbJobs = [];

        #region Fetch missing cache entries from DB

        if (cacheMissIds.Count != 0)
        {
            _logger.Debug("Cache miss: {Count} jobs, fetching from database", cacheMissIds.Count);

            // Normal query - Contains() works better without compiled query
            var dbJobsList = await dbContext.ScheduledJobs
                                            .AsNoTracking()
                                            .Where(j => cacheMissIds.Contains(j.Id) && j.IsActive)
                                            .Select(ScheduledJob.Projections.CacheJob)
                                            .ToListAsync(cancellationToken);

            dbJobs = dbJobsList.ToDictionary(j => j.Id);

            // Remove stale Redis entries for jobs not found in DB
            var notFoundIds = cacheMissIds.Except(dbJobs.Keys).ToList();

            if (notFoundIds.Count > 0)
            {
                _logger.Warning("Found {Count} stale job references in Redis (not in DB), cleaning up: {JobIds}", notFoundIds.Count, string.Join(", ", notFoundIds));

                foreach (var staleId in notFoundIds)
                {
                    await _redisScheduler.RemoveFromScheduledSetAsync(staleId, cancellationToken);
                    await _redisScheduler.RemoveCachedJobAsync(staleId, cancellationToken);
                }
            }

            // Cache warming: Add fetched jobs to cache
            foreach (var job in dbJobsList)
                await _redisScheduler.CacheJobDetailsAsync(job, ttl: TimeSpan.FromHours(24), cancellationToken);

            _logger.Debug("Cache warming: {Count} jobs added to Redis cache", dbJobsList.Count);
        }

        #endregion

        // 5. Merge cached and DB jobs
        var allJobs = cachedJobs.Concat(dbJobs).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        // 5.1. Populate ExecuteAt from ZSET for ALL jobs (since it's no longer in cache/DB)
        // Use bulk pipeline for efficiency (single Redis round-trip)
        var executeAtDict = await _redisScheduler.GetScheduledTimesBulkAsync(allJobs.Keys, cancellationToken);

        foreach (var (jobId, executeAt) in executeAtDict)
            if (executeAt.HasValue && allJobs.TryGetValue(jobId, out var job))
                job.ExecuteAt = executeAt.Value;

        return allJobs;
    }

    /// <summary>
    /// Dispatches a job to RabbitMQ (worker checks already done).
    /// </summary>
    /// <returns>True if successfully published, false otherwise</returns>
    private async Task<bool> DispatchJobToRabbitMQAsync(ScheduledJob job, JobOccurrence occurrence, CancellationToken cancellationToken)
    {
        // Acquire distributed lock to prevent duplicate dispatch
        // Multiple dispatcher instances might try to dispatch the same job simultaneously
        var lockAcquired = await _redisLock.TryAcquireLockAsync(job.Id,
                                                                Environment.MachineName, // Use machine name as lock owner identifier
                                                                TimeSpan.FromSeconds(_options.LockTtlSeconds),
                                                                cancellationToken);

        if (!lockAcquired)
        {
            _logger.Warning("Failed to acquire lock for job {JobId}. Another dispatcher instance may be handling it.", job.Id);

            // Another instance is handling this job, mark occurrence as redundant
            occurrence.Status = JobOccurrenceStatus.Failed;
            occurrence.Exception = "Duplicate dispatch prevented by distributed lock";
            occurrence.Logs.Add(new OccurrenceLog
            {
                Timestamp = DateTime.UtcNow,
                Level = "Warning",
                Message = "Failed to acquire dispatch lock - another instance is handling this job",
                Category = "Dispatcher",
                Data = new Dictionary<string, object>
                {
                    ["LockOwner"] = await _redisLock.GetLockOwnerAsync(job.Id, cancellationToken) ?? "unknown"
                }
            });

            return false;
        }

        try
        {
            // Publish to RabbitMQ with CorrelationId
            var published = await _rabbitMQPublisher.PublishJobAsync(job, occurrence.CorrelationId, cancellationToken);

            if (!published)
            {
                _logger.Error("Failed to publish job {JobId} to RabbitMQ", job.Id);

                occurrence.Status = JobOccurrenceStatus.Failed;
                occurrence.Exception = "Failed to publish to RabbitMQ";
                occurrence.Logs.Add(new OccurrenceLog
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Error",
                    Message = "Failed to publish job to RabbitMQ queue",
                    Category = "Dispatcher"
                });

                return false;
            }

            // CRITICAL: Reschedule recurring job IMMEDIATELY after successful publish
            // This prevents race condition where dispatcher picks up the same job again
            // before worker finishes execution (job completes at T+10s, but dispatcher checks at T+5s)
            await HandleRecurringJobAsync(job, cancellationToken);

            _logger.Debug("Job {JobId} ({JobType}) dispatched successfully with CorrelationId {CorrelationId}", job.Id, job.JobNameInWorker, occurrence.Id);

            return true;
        }
        finally
        {
            // Always release lock in finally block
            var released = await _redisLock.ReleaseLockAsync(job.Id, Environment.MachineName, cancellationToken);

            if (!released)
            {
                _logger.Warning("Failed to release lock for job {JobId}. Lock may have expired or was released by another instance.", job.Id);
            }
        }
    }

    /// <summary>
    /// Handles recurring job logic (reschedules if needed).
    /// For one-time jobs, removes from Redis scheduled set (if not already removed).
    /// NOTE: This method may be called when:
    /// 1. Job is skipped due to ConcurrentExecutionPolicy (already removed from scheduled set)
    /// 2. Job is successfully dispatched (not yet removed from scheduled set for one-time jobs)
    /// </summary>
    private async Task HandleRecurringJobAsync(ScheduledJob job, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.CronExpression))
        {
            // One-time job - ensure it's removed from Redis scheduled set
            // Safe to call multiple times (idempotent operation)
            await _redisScheduler.RemoveFromScheduledSetAsync(job.Id, cancellationToken);

            _logger.Debug("One-time job {JobId} removed from Redis scheduled set", job.Id);

            return;
        }

        try
        {
            // Parse cron expression
            var cronExpression = CronExpression.Parse(job.CronExpression, CronFormat.IncludeSeconds);

            // Calculate next occurrence
            var nextRun = cronExpression.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Utc);

            if (nextRun.HasValue)
            {
                // Update job's ExecuteAt in memory
                job.ExecuteAt = nextRun.Value;

                // Update Redis ZSET with new score (future timestamp)
                // This reschedules the job for next execution
                await _redisScheduler.UpdateScheduleAsync(job.Id, nextRun.Value, cancellationToken);

                _logger.Debug("Recurring job {JobId} rescheduled for {NextRun} (Cron: {CronExpression})", job.Id, nextRun.Value, job.CronExpression);
            }
            else
            {
                _logger.Warning("Cron expression {CronExpression} has no future occurrences, removing job {JobId}", job.CronExpression, job.Id);

                await _redisScheduler.RemoveFromScheduledSetAsync(job.Id, cancellationToken);
                await _redisScheduler.RemoveCachedJobAsync(job.Id, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to parse cron expression '{CronExpression}' for job {JobId}", job.CronExpression, job.Id);

            // Remove invalid cron job from Redis
            await _redisScheduler.RemoveFromScheduledSetAsync(job.Id, cancellationToken);
            await _redisScheduler.RemoveCachedJobAsync(job.Id, cancellationToken);
        }
    }

    /// <summary>
    /// Performs startup recovery (zombie detection and Redis repopulation).
    /// </summary>
    private async Task PerformStartupRecoveryAsync(CancellationToken cancellationToken)
    {
        _logger.Information("Starting startup recovery...");

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

        // STEP 1: Clean up stale Redis data before repopulation
        _logger.Information("Cleaning up stale Redis scheduled jobs...");

        try
        {
            // Get all scheduled job IDs from Redis
            var allScheduledInRedis = await _redisScheduler.GetDueJobsAsync(DateTime.UtcNow.AddYears(100), int.MaxValue, cancellationToken);

            if (allScheduledInRedis.Count > 0)
            {
                _logger.Information("Found {Count} jobs in Redis ZSET, validating against database...", allScheduledInRedis.Count);

                // Get all active job IDs from database
                var activeJobIdsInDb = await dbContext.ScheduledJobs
                                                      .AsNoTracking()
                                                      .Where(j => j.IsActive)
                                                      .Select(j => j.Id)
                                                      .ToListAsync(cancellationToken);

                var activeJobIdsSet = activeJobIdsInDb.ToHashSet();

                // Find stale jobs (in Redis but not active in DB)
                var staleJobIds = allScheduledInRedis.Except(activeJobIdsSet).ToList();

                if (staleJobIds.Count > 0)
                {
                    _logger.Warning("Found {Count} stale jobs in Redis (not active in DB), removing...", staleJobIds.Count);

                    foreach (var staleId in staleJobIds)
                    {
                        await _redisScheduler.RemoveFromScheduledSetAsync(staleId, cancellationToken);
                        await _redisScheduler.RemoveCachedJobAsync(staleId, cancellationToken);
                    }

                    _logger.Information("Cleaned up {Count} stale Redis entries", staleJobIds.Count);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to clean up stale Redis data, continuing with recovery...");
        }

        // STEP 2: Clean up zombie occurrences from previous run
        _logger.Information("Cleaning up zombie occurrences from previous run...");

        try
        {
            var zombieStatuses = new[]
            {
                JobOccurrenceStatus.Queued,
                JobOccurrenceStatus.Running
            };

            // Grace period: Only mark as zombie if older than 2 minutes
            // This gives running jobs time to complete after restart
            var zombieThreshold = DateTime.UtcNow.AddMinutes(-2);

            // Mark old Queued/Running occurrences as Failed (system restart)
            var zombieOccurrences = await dbContext.JobOccurrences.Where(o => zombieStatuses.Contains(o.Status) && o.CreatedAt < zombieThreshold).ToListAsync(cancellationToken);

            if (zombieOccurrences.Count > 0)
            {
                _logger.Warning("Found {Count} zombie occurrences from previous run (older than 2 minutes), marking as Failed...", zombieOccurrences.Count);

                foreach (var occurrence in zombieOccurrences)
                {
                    occurrence.Status = JobOccurrenceStatus.Failed;
                    occurrence.EndTime = DateTime.UtcNow;
                    occurrence.Exception = "System restart detected. Job was not completed before shutdown.";
                    occurrence.Logs.Add(new OccurrenceLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Warning",
                        Message = "Job marked as failed due to system restart (grace period: 2 minutes)",
                        Category = "StartupRecovery"
                    });
                }

                await dbContext.SaveChangesAsync(cancellationToken);

                _logger.Information("Cleaned up {Count} zombie occurrences", zombieOccurrences.Count);
            }
            else
            {
                _logger.Information("No zombie occurrences found (all recent running jobs still have grace period)");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to clean up zombie occurrences, continuing with recovery...");
        }

        // STEP 3: Repopulate Redis with active jobs from database
        _logger.Information("Repopulating Redis with active jobs from database...");

        var activeJobs = await _getAllActiveJobsCompiled(dbContext).ToListAsync(cancellationToken);

        var addedCount = 0;
        var updatedCount = 0;
        var cachedCount = 0;

        foreach (var job in activeJobs)
        {
            try
            {
                // Check if job already exists in Redis
                var existingScheduleTime = await _redisScheduler.GetScheduledTimeAsync(job.Id, cancellationToken);

                if (existingScheduleTime.HasValue)
                {
                    // Job exists, update if schedule time changed
                    if (existingScheduleTime.Value != job.ExecuteAt)
                    {
                        await _redisScheduler.UpdateScheduleAsync(job.Id, job.ExecuteAt, cancellationToken);

                        updatedCount++;

                        _logger.Debug("Updated schedule for job {JobId}: {OldTime} → {NewTime}", job.Id, existingScheduleTime.Value, job.ExecuteAt);
                    }
                }
                else
                {
                    // Job doesn't exist, add it
                    var added = await _redisScheduler.AddToScheduledSetAsync(job.Id, job.ExecuteAt, cancellationToken);

                    if (added)
                        addedCount++;
                }

                // Cache job details
                var cached = await _redisScheduler.CacheJobDetailsAsync(job, ttl: TimeSpan.FromHours(24), cancellationToken);

                if (cached)
                    cachedCount++;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to process job {JobId} during startup recovery", job.Id);
            }
        }

        _logger.Information("Startup recovery completed. Redis ZSET: {Added} added, {Updated} updated. Cache: {Cached} warmed. Total active jobs: {Total}",
            addedCount, updatedCount, cachedCount, activeJobs.Count);
    }

    /// <summary>
    /// Waits for database to be ready and migrations to complete.
    /// </summary>
    private async Task WaitForDatabaseReadyAsync(CancellationToken cancellationToken)
    {
        const int maxRetries = 30; // 30 attempts
        const int delaySeconds = 2; // Wait 2 seconds between attempts

        _logger.Information("Waiting for database migrations to complete...");

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await using var scope = _serviceProvider.CreateAsyncScope();

                var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

                // Check if JobOccurrences table exists (created in migration 002)
                var tableExists = await dbContext.Database.CanConnectAsync(cancellationToken);

                if (tableExists)
                {
                    // Verify JobOccurrences table specifically
                    var occurrenceExists = await dbContext.JobOccurrences.AsNoTracking().AnyAsync(cancellationToken);

                    _logger.Information("Database migrations completed. JobOccurrences table is ready.");

                    return;
                }
            }
            catch (Exception ex) when (ex.Message.Contains("does not exist") || ex.Message.Contains("relation") || ex.Message.Contains("table"))
            {
                _logger.Information("Database not ready yet (attempt {Attempt}/{MaxRetries}). Waiting {Delay}s...", attempt, maxRetries, delaySeconds);

                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unexpected error checking database readiness (attempt {Attempt}/{MaxRetries})", attempt, maxRetries);

                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
            }
        }

        throw new InvalidOperationException($"Database did not become ready after {maxRetries} attempts ({maxRetries * delaySeconds}s). Ensure migrations are running and database is accessible.");
    }

    /// <summary>
    /// Retries occurrences that failed to dispatch (with exponential backoff).
    /// </summary>
    private async Task RetryFailedDispatchesAsync(CancellationToken cancellationToken)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

        // Get occurrences that are ready for retry (status = Queued, NextDispatchRetryAt <= now, DispatchRetryCount < 5)
        var now = DateTime.UtcNow;

        // Max 5 retry attempts (total ~4 minutes of retries)
        const int maxRetryAttempts = 5;

        // Use compiled query
        var retryOccurrences = await _getRetryOccurrencesCompiled(dbContext, now, maxRetryAttempts).ToListAsync(cancellationToken);

        if (retryOccurrences.Count == 0)
            return;

        _logger.Debug("Retrying {Count} failed dispatch occurrences", retryOccurrences.Count);

        var jobIds = retryOccurrences.Select(o => o.JobId).Distinct().ToList();

        // Normal query - Contains() works better without compiled query
        var jobsList = await dbContext.ScheduledJobs
                                      .AsNoTracking()
                                      .Where(j => jobIds.Contains(j.Id))
                                      .Select(ScheduledJob.Projections.RetryFailedOccurrence)
                                      .ToListAsync(cancellationToken);

        var jobs = jobsList.ToDictionary(j => j.Id);

        var successfulRetries = new List<JobOccurrence>();
        var failedRetries = new List<JobOccurrence>();

        foreach (var occurrence in retryOccurrences)
        {
            if (!jobs.TryGetValue(occurrence.JobId, out var job))
            {
                _logger.Debug("Job {JobId} not found for occurrence {OccurrenceId}, marking as failed", occurrence.JobId, occurrence.Id);

                occurrence.Status = JobOccurrenceStatus.Failed;
                occurrence.Exception = "Job not found during retry";
                occurrence.NextDispatchRetryAt = null;
                failedRetries.Add(occurrence);

                continue;
            }

            try
            {
                // Retry dispatch
                var published = await _rabbitMQPublisher.PublishJobAsync(job, occurrence.CorrelationId, cancellationToken);

                if (published)
                {
                    occurrence.NextDispatchRetryAt = null; // Clear retry schedule
                    occurrence.Logs.Add(new OccurrenceLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Information",
                        Message = $"Dispatch retry successful (attempt {occurrence.DispatchRetryCount})",
                        Category = "Dispatcher"
                    });

                    successfulRetries.Add(occurrence);

                    _logger.Debug("Occurrence {OccurrenceId} successfully dispatched on retry attempt {Attempt}", occurrence.Id, occurrence.DispatchRetryCount);
                }
                else
                {
                    // Retry failed again - schedule next retry
                    if (occurrence.DispatchRetryCount >= maxRetryAttempts - 1)
                    {
                        // Max retries reached, mark as failed
                        occurrence.Status = JobOccurrenceStatus.Failed;
                        occurrence.Exception = $"Failed to dispatch after {maxRetryAttempts} attempts";
                        occurrence.NextDispatchRetryAt = null;

                        occurrence.Logs.Add(new OccurrenceLog
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Error",
                            Message = $"Max dispatch retry attempts reached ({maxRetryAttempts}). Marking as failed.",
                            Category = "Dispatcher"
                        });

                        // Remove job from Redis scheduled set (no more retries)
                        if (job != null && string.IsNullOrWhiteSpace(job.CronExpression))
                        {
                            try
                            {
                                await _redisScheduler.RemoveFromScheduledSetAsync(job.Id, cancellationToken);

                                _logger.Debug("Removed one-time job {JobId} from Redis after max dispatch retries", job.Id);
                            }
                            catch (Exception ex)
                            {
                                _logger.Warning(ex, "Failed to cleanup Redis for job {JobId}", job.Id);
                            }
                        }

                        _logger.Debug("Occurrence {OccurrenceId} failed after {MaxAttempts} retry attempts", occurrence.Id, maxRetryAttempts);
                    }
                    else
                    {
                        // Schedule next retry
                        var retryDelaySeconds = Math.Min(30 * Math.Pow(2, occurrence.DispatchRetryCount), 120);

                        occurrence.NextDispatchRetryAt = DateTime.UtcNow.AddSeconds(retryDelaySeconds);
                        occurrence.DispatchRetryCount++;
                        occurrence.Logs.Add(new OccurrenceLog
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Warning",
                            Message = $"Dispatch retry failed (attempt {occurrence.DispatchRetryCount}). Next retry at {occurrence.NextDispatchRetryAt:HH:mm:ss}",
                            Category = "Dispatcher"
                        });
                    }

                    failedRetries.Add(occurrence);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Exception during dispatch retry for occurrence {OccurrenceId}", occurrence.Id);

                if (occurrence.DispatchRetryCount >= maxRetryAttempts - 1)
                {
                    occurrence.Status = JobOccurrenceStatus.Failed;
                    occurrence.Exception = $"Exception during retry: {ex.Message}";
                    occurrence.NextDispatchRetryAt = null;

                    // Remove job from Redis scheduled set (no more retries)
                    if (job != null && string.IsNullOrWhiteSpace(job.CronExpression))
                    {
                        try
                        {
                            await _redisScheduler.RemoveFromScheduledSetAsync(job.Id, cancellationToken);

                            _logger.Debug("Removed one-time job {JobId} from Redis after exception in dispatch retry", job.Id);
                        }
                        catch (Exception cleanupEx)
                        {
                            _logger.Debug(cleanupEx, "Failed to cleanup Redis for job {JobId}", job.Id);
                        }
                    }
                }
                else
                {
                    var retryDelaySeconds = Math.Min(30 * Math.Pow(2, occurrence.DispatchRetryCount), 120);

                    occurrence.NextDispatchRetryAt = DateTime.UtcNow.AddSeconds(retryDelaySeconds);
                    occurrence.DispatchRetryCount++;
                }

                failedRetries.Add(occurrence);
            }
        }

        // Bulk update all retry results
        if (successfulRetries.Count > 0 || failedRetries.Count > 0)
        {
            var allRetries = successfulRetries.Concat(failedRetries).ToList();

            await dbContext.BulkUpdateAsync(allRetries, (bc) =>
            {
                bc.PropertiesToInclude = bc.PropertiesToIncludeOnUpdate = _updatePropNames;
            }, cancellationToken: cancellationToken);

            _logger.Information("Dispatch retry completed: {Success} successful, {Failed} failed", successfulRetries.Count, failedRetries.Count);
        }
    }
}
