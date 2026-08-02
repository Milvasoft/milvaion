using Cronos;
using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Dtos.AlertingDtos;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Interfaces.RabbitMQ;
using Milvaion.Application.Interfaces.Redis;
using Milvaion.Application.Utils.Constants;
using Milvaion.Infrastructure.BackgroundServices.Base;
using Milvaion.Infrastructure.Persistence.Context;
using Milvaion.Infrastructure.Scheduling;
using Milvaion.Infrastructure.Telemetry;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Helpers;
using Milvasoft.Milvaion.Sdk.Utils;
using System.Collections.Concurrent;
using System.Diagnostics;

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
                                  IRedisStatsService redisStatsService,
                                  IRabbitMQPublisher rabbitMQPublisher,
                                  IAlertNotifier alertNotifier,
                                  IOptions<JobDispatcherOptions> options,
                                  IDispatcherControlService controlService,
                                  ILoggerFactory loggerFactory,
                                  BackgroundServiceMetrics metrics,
                                  IMemoryStatsRegistry memoryStatsRegistry = null) : MemoryTrackedBackgroundService(loggerFactory, options.Value, memoryStatsRegistry)
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IRedisSchedulerService _redisScheduler = redisScheduler;
    private readonly IRedisLockService _redisLock = redisLock;
    private readonly IRedisWorkerService _redisWorkerService = redisWorkerService;
    private readonly IRedisStatsService _redisStatsService = redisStatsService;
    private readonly IRabbitMQPublisher _rabbitMQPublisher = rabbitMQPublisher;
    private readonly IAlertNotifier _alertNotifier = alertNotifier;
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<JobDispatcherService>();
    private readonly JobDispatcherOptions _options = options.Value;
    private readonly IDispatcherControlService _controlService = controlService;
    private readonly BackgroundServiceMetrics _metrics = metrics;
    private readonly static List<string> _updatePropNames =
    [
        nameof(JobOccurrence.Status),
        nameof(JobOccurrence.Exception),
        nameof(JobOccurrence.DispatchRetryCount),
        nameof(JobOccurrence.NextDispatchRetryAt)
    ];
    private readonly static List<string> _startupRecoveryUpdatePropNames =
    [
        nameof(JobOccurrence.Status),
        nameof(JobOccurrence.EndTime),
        nameof(JobOccurrence.Exception)
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
                   .Take(1000));

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

                    _alertNotifier.SendFireAndForget(AlertType.ServiceDegraded, new AlertPayload
                    {
                        Title = "Job Dispatcher Degraded",
                        Message = $"Job dispatcher entered degraded mode after {consecutiveFailures} consecutive failures. Backing off for {backoffSeconds}s.",
                        Severity = AlertSeverity.Critical,
                        Source = nameof(JobDispatcherService),
                        ThreadKey = "service-degraded-dispatcher"
                    });

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
        var sw = Stopwatch.StartNew();

        // 1. Get due jobs from Redis ZSET
        var dueJobIds = await GetDueJobIdsFromRedisAsync(cancellationToken);

        if (dueJobIds.IsNullOrEmpty())
            return;

        _logger.Debug("Found {Count} due jobs in Redis", dueJobIds.Count);

        // 2. Fetch job details from cache/DB
        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();
        var allJobs = await GetDueJobsAsync(dbContext, dueJobIds, cancellationToken);

        // 3. Check running jobs and get capacities
        var runningJobIdsSet = await _redisScheduler.GetRunningJobIdsAsync(allJobs.Keys.ToList(), cancellationToken);
        var workerCapacities = await GetWorkerCapacitiesAsync(allJobs.Values, cancellationToken);
        var consumerCapacities = await GetConsumerCapacitiesAsync(allJobs.Values, cancellationToken);

        // 4. Validate and filter jobs
        var (jobsPassedValidation, toRemovedIdListScheduledSet) = ValidateAndFilterJobs(dueJobIds, allJobs, runningJobIdsSet, workerCapacities, consumerCapacities);

        _logger.Debug("Pre-validation: {Passed}/{Total} jobs passed", jobsPassedValidation.Count, dueJobIds.Count);

        // 5. Acquire locks and create occurrences
        var (occurrences, occurrencesAsEvents, logsToInsert, jobsToDispatch) = await AcquireLocksAndCreateOccurrencesAsync(jobsPassedValidation, cancellationToken);

        // 6. Remove invalid jobs from Redis
        if (!toRemovedIdListScheduledSet.IsNullOrEmpty())
        {
            _logger.Debug("Removed {Count} invalid jobs from Redis scheduled set", toRemovedIdListScheduledSet.Count);
            await _redisScheduler.RemoveFromScheduledSetBulkAsync(toRemovedIdListScheduledSet, cancellationToken);
        }

        if (occurrences.IsNullOrEmpty())
        {
            _logger.Debug("No jobs to dispatch after pre-checks");
            return;
        }

        _logger.Debug("Created {Count} job occurrences, dispatching to RabbitMQ", occurrences.Count);

        // 7. Persist occurrences to database
        await using var scope2 = _serviceProvider.CreateAsyncScope();
        var jobOccurrenceRepository = scope2.ServiceProvider.GetRequiredService<IMilvaionRepositoryBase<JobOccurrence>>();

        await PersistOccurrencesAsync(scope2, dbContext, jobOccurrenceRepository, allJobs, occurrences, occurrencesAsEvents, cancellationToken);

        // 8. Record metrics and update Redis counters
        RecordDispatchMetrics(sw, occurrences.Count);

        await UpdateRedisCountersAfterDispatchAsync(occurrences.Count, cancellationToken);

        // 9. Dispatch to RabbitMQ
        var failedToDispatchOccurrences = await DispatchJobsToRabbitMQAsync(jobsToDispatch, occurrences, logsToInsert, cancellationToken);

        // 10. Handle failed dispatches
        await HandleFailedDispatchesAsync(scope2, jobsToDispatch, failedToDispatchOccurrences, logsToInsert, cancellationToken);

        // 11. Mark one-time jobs as finished
        await MarkCompletedOneTimeJobsAsync(dbContext, jobsToDispatch, failedToDispatchOccurrences, cancellationToken);

        // 12. Insert logs and retry failed dispatches
        var jobOccurrenceLogRepository = scope2.ServiceProvider.GetRequiredService<IMilvaionRepositoryBase<JobOccurrenceLog>>();

        if (!logsToInsert.IsEmpty)
        {
            await jobOccurrenceLogRepository.BulkAddAsync([.. logsToInsert], cancellationToken: cancellationToken);
            _logger.Debug("Bulk inserted {Count} logs from dispatch failures/retries", logsToInsert.Count);
        }

        await RetryFailedDispatchesAsync(cancellationToken);
    }

    /// <summary>
    /// Stamps one-time jobs that were dispatched in this cycle as finished.
    /// </summary>
    /// <remarks>
    /// A one-time job runs once and must never be scheduled again. Until this column
    /// existed the only record of that was the job's absence from the Redis sorted set,
    /// and Redis is a cache: a flush or a restart erased the fact and startup recovery
    /// put the job back, running it a second time.
    ///
    /// One statement per dispatch cycle, and only for jobs without a cron expression -
    /// so in a recurring-only installation it never runs at all. A one-time job is
    /// stamped once in its life, so this does not grow with traffic.
    /// </remarks>
    private async Task MarkCompletedOneTimeJobsAsync(MilvaionDbContext dbContext,
                                                     List<ScheduledJob> jobsToDispatch,
                                                     ConcurrentBag<JobOccurrence> failedToDispatchOccurrences,
                                                     CancellationToken cancellationToken)
    {
        if (jobsToDispatch.Count == 0)
            return;

        // A job whose publish failed has not run. It keeps its schedule and is retried,
        // so stamping it here would silently drop it.
        var failedJobIds = failedToDispatchOccurrences.Select(o => o.JobId).ToHashSet();

        var completedIds = jobsToDispatch.Where(j => string.IsNullOrWhiteSpace(j.CronExpression) && !failedJobIds.Contains(j.Id))
                                         .Select(j => j.Id)
                                         .ToList();

        if (completedIds.Count == 0)
            return;

        try
        {
            var stampedAt = DateTime.UtcNow;

            // Guarded on CompletedAt being null so a re-dispatch cannot move the timestamp
            // forward; the first run is the one that counts.
            var stamped = await dbContext.ScheduledJobs
                                         .Where(j => completedIds.Contains(j.Id) && j.CompletedAt == null)
                                         .ExecuteUpdateAsync(setters => setters.SetProperty(j => j.CompletedAt, stampedAt), cancellationToken);

            _logger.Debug("Marked {Count} one-time job(s) as completed", stamped);
        }
        catch (Exception ex)
        {
            // The jobs were dispatched successfully; failing the cycle over the bookkeeping
            // would be worse than a late stamp. The next cycle does not retry this, but the
            // job is already out of the sorted set, so the only exposure is a restart before
            // it is stamped - which is the behaviour that existed before this method.
            _logger.Error(ex, "Failed to mark one-time jobs as completed: {JobIds}", string.Join(", ", completedIds));
        }
    }

    /// <summary>
    /// Gets due job IDs from Redis ZSET.
    /// </summary>
    private async Task<List<Guid>> GetDueJobIdsFromRedisAsync(CancellationToken cancellationToken)
    {
        try
        {
            var dueJobIds = await _redisScheduler.GetDueJobsAsync(DateTime.UtcNow, _options.BatchSize, cancellationToken);
            _metrics.SetPendingJobsCount(dueJobIds.Count);
            return dueJobIds;
        }
        catch (Exception ex) when (ex.Message.Contains("Circuit breaker") || ex.Message.Contains("Redis"))
        {
            _logger.Error(ex, "Redis unavailable - circuit breaker may be open. Skipping this dispatch iteration.");
            _metrics.RecordDispatchFailure("redis_unavailable");
            return [];
        }
    }

    /// <summary>
    /// Gets worker capacities for all unique workers.
    /// </summary>
    private async Task<Dictionary<string, (int currentJobs, int? maxParallelJobs)>> GetWorkerCapacitiesAsync(IEnumerable<ScheduledJob> jobs, CancellationToken cancellationToken)
    {
        var workerIds = jobs.Select(j => j.WorkerId).Where(w => !string.IsNullOrWhiteSpace(w)).Distinct().ToList();
        var workerCapacities = new Dictionary<string, (int currentJobs, int? maxParallelJobs)>();

        foreach (var workerId in workerIds)
        {
            var isActive = await _redisWorkerService.IsWorkerActiveAsync(workerId, cancellationToken);

            if (!isActive)
            {
                workerCapacities[workerId] = (-1, null); // Mark as inactive
                continue;
            }

            var capacity = await _redisWorkerService.GetWorkerCapacityAsync(workerId, cancellationToken);
            workerCapacities[workerId] = capacity;
        }

        return workerCapacities;
    }

    /// <summary>
    /// Gets consumer capacities for all unique worker+jobType combinations.
    /// </summary>
    private async Task<Dictionary<string, (int currentJobs, int? maxParallelJobs)>> GetConsumerCapacitiesAsync(IEnumerable<ScheduledJob> jobs, CancellationToken cancellationToken)
    {
        var workerJobTypePairs = jobs.Where(j => !string.IsNullOrWhiteSpace(j.WorkerId))
                                     .Select(j => (j.WorkerId, JobType: j.JobNameInWorker))
                                     .Distinct()
                                     .ToList();

        var consumerCapacities = new Dictionary<string, (int currentJobs, int? maxParallelJobs)>();

        foreach (var (workerId, jobType) in workerJobTypePairs)
        {
            var key = $"{workerId}:{jobType}";
            var capacity = await _redisWorkerService.GetConsumerCapacityAsync(workerId, jobType, cancellationToken);
            consumerCapacities[key] = capacity;
        }

        return consumerCapacities;
    }

    /// <summary>
    /// Validates jobs and filters based on concurrency policy and worker capacity.
    /// </summary>
    private (List<ScheduledJob> PassedJobs, List<Guid> ToRemoveFromRedis) ValidateAndFilterJobs(
        List<Guid> dueJobIds,
        Dictionary<Guid, ScheduledJob> allJobs,
        HashSet<Guid> runningJobIdsSet,
        Dictionary<string, (int currentJobs, int? maxParallelJobs)> workerCapacities,
        Dictionary<string, (int currentJobs, int? maxParallelJobs)> consumerCapacities)
    {
        var jobsPassedValidation = new List<ScheduledJob>();
        var toRemovedIdListScheduledSet = new List<Guid>();

        foreach (var jobId in dueJobIds)
        {
            // Validate job existence and active status
            if (!allJobs.TryGetValue(jobId, out var job) || !job.IsActive)
            {
                toRemovedIdListScheduledSet.Add(jobId);
                continue;
            }

            if (!CanDispatchBasedOnConcurrencyPolicy(runningJobIdsSet, job))
                continue;

            if (!CanDispatchBasedOnWorkerCapacity(job, workerCapacities, consumerCapacities))
                continue;

            jobsPassedValidation.Add(job);
        }

        return (jobsPassedValidation, toRemovedIdListScheduledSet);
    }

    /// <summary>
    /// Acquires distributed locks and creates job occurrences.
    /// </summary>
    private async Task<(List<JobOccurrence> Occurrences, List<JobOccurrence> OccurrencesAsEvents, ConcurrentBag<JobOccurrenceLog> LogsToInsert, List<ScheduledJob> JobsToDispatch)>
        AcquireLocksAndCreateOccurrencesAsync(List<ScheduledJob> jobsPassedValidation, CancellationToken cancellationToken)
    {
        var occurrences = new List<JobOccurrence>();
        var occurrencesAsEvents = new List<JobOccurrence>();
        var logsToInsert = new ConcurrentBag<JobOccurrenceLog>();
        var jobsToDispatch = new List<ScheduledJob>();

        // Bulk lock -> Single Redis call using Lua script
        var jobIdsToLock = jobsPassedValidation.Select(j => j.Id).ToList();
        var lockResults = await _redisLock.TryAcquireLocksBulkAsync(jobIdsToLock, Environment.MachineName, TimeSpan.FromSeconds(_options.LockTtlSeconds), cancellationToken);

        // Create occurrences only for jobs with acquired locks
        foreach (var job in jobsPassedValidation)
        {
            if (!lockResults.TryGetValue(job.Id, out var hasLock) || !hasLock)
                continue;

            var correlationId = Guid.CreateVersion7();

            var occurrence = new JobOccurrence
            {
                Id = correlationId,
                CorrelationId = correlationId,
                JobId = job.Id,
                JobName = job.JobNameInWorker,
                JobDisplayName = job.DisplayName,
                JobVersion = job.Version,
                ZombieTimeoutMinutes = job.ZombieTimeoutMinutes,
                ExecutionTimeoutSeconds = job.ExecutionTimeoutSeconds,
                WorkerId = job.WorkerId,
                Status = JobOccurrenceStatus.Queued,
                CreatedAt = DateTime.UtcNow,
                CreatorUserName = GlobalConstant.SystemUsername
            };

            occurrences.Add(occurrence);
            jobsToDispatch.Add(job);

            logsToInsert.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OccurrenceId = correlationId,
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
            });

            occurrencesAsEvents.Add(new JobOccurrence
            {
                Id = correlationId,
                JobId = job.Id,
                JobName = job.DisplayName,
                JobDisplayName = job.DisplayName,
                Status = JobOccurrenceStatus.Queued,
                WorkerId = job.WorkerId,
                CreatedAt = DateTime.UtcNow,
            });
        }

        return (occurrences, occurrencesAsEvents, logsToInsert, jobsToDispatch);
    }

    /// <summary>
    /// Persists occurrences to database and publishes events.
    /// </summary>
    private async Task PersistOccurrencesAsync(
        AsyncServiceScope scope,
        MilvaionDbContext dbContext,
        IMilvaionRepositoryBase<JobOccurrence> jobOccurrenceRepository,
        Dictionary<Guid, ScheduledJob> allJobs,
        List<JobOccurrence> occurrences,
        List<JobOccurrence> occurrencesAsEvents,
        CancellationToken cancellationToken)
    {
        try
        {
            await jobOccurrenceRepository.BulkAddAsync(occurrences, cancellationToken: cancellationToken);

            try
            {
                await _redisStatsService.IncrementTotalOccurrencesAsync(occurrences.Count, cancellationToken);
                await _redisStatsService.TrackExecutionsAsync([.. occurrences.Select(o => o.Id)], cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to update stats counters (non-critical)");
            }

            var eventPublisher = scope.ServiceProvider.GetService<IJobOccurrenceEventPublisher>();
            await eventPublisher.PublishOccurrenceCreatedAsync(occurrencesAsEvents, _logger, cancellationToken);
        }
        catch (DbUpdateException)
        {
            await HandleDbUpdateExceptionAsync(dbContext, jobOccurrenceRepository, allJobs, occurrences, cancellationToken);
        }
    }

    /// <summary>
    /// Handles DbUpdateException by removing stale jobs from Redis.
    /// </summary>
    private async Task HandleDbUpdateExceptionAsync(
        MilvaionDbContext dbContext,
        IMilvaionRepositoryBase<JobOccurrence> jobOccurrenceRepository,
        Dictionary<Guid, ScheduledJob> allJobs,
        List<JobOccurrence> occurrences,
        CancellationToken cancellationToken)
    {
        var jobIdsToVerify = allJobs.Keys.ToList();

        var existingJobIds = await dbContext.ScheduledJobs
                                            .AsNoTracking()
                                            .Where(j => jobIdsToVerify.Contains(j.Id))
                                            .Select(j => j.Id)
                                            .ToListAsync(cancellationToken);

        var existingJobIdsSet = existingJobIds.ToHashSet();
        var invalidJobIds = jobIdsToVerify.Except(existingJobIdsSet).ToList();

        if (!invalidJobIds.IsNullOrEmpty())
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
    }

    /// <summary>
    /// Records dispatch metrics.
    /// </summary>
    private void RecordDispatchMetrics(Stopwatch sw, int dispatchCount)
    {
        _metrics.RecordJobsDispatched(dispatchCount);
        _metrics.RecordDispatchDuration(sw.Elapsed.TotalMilliseconds, dispatchCount);
    }

    /// <summary>
    /// Updates Redis counters after successful dispatch.
    /// </summary>
    private async Task UpdateRedisCountersAfterDispatchAsync(int dispatchCount, CancellationToken cancellationToken)
    {
        if (dispatchCount <= 0)
            return;

        try
        {
            await _redisStatsService.IncrementStatusCounterAsync(JobOccurrenceStatus.Queued, dispatchCount, cancellationToken);
            _logger.Debug("Incremented Queued counter by {Count} for successfully dispatched jobs", dispatchCount);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to increment Queued counter (non-critical)");
        }
    }

    /// <summary>
    /// Dispatches jobs to RabbitMQ in parallel.
    /// </summary>
    private async Task<ConcurrentBag<JobOccurrence>> DispatchJobsToRabbitMQAsync(
        List<ScheduledJob> jobsToDispatch,
        List<JobOccurrence> occurrences,
        ConcurrentBag<JobOccurrenceLog> logsToInsert,
        CancellationToken cancellationToken)
    {
        var failedToDispatchOccurrences = new ConcurrentBag<JobOccurrence>();
        var occurrenceByJobId = occurrences.ToDictionary(o => o.JobId);

        await Parallel.ForEachAsync(jobsToDispatch, new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = cancellationToken
        }, async (job, ct) =>
        {
            var occurrence = occurrenceByJobId[job.Id];

            try
            {
                var success = await DispatchJobToRabbitMQAsync(job, occurrence, logsToInsert, ct);

                if (!success)
                    failedToDispatchOccurrences.Add(occurrence);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                occurrence.Status = JobOccurrenceStatus.Failed;
                occurrence.Exception = $"Dispatch exception: {ex.Message}";

                logsToInsert.Add(new JobOccurrenceLog
                {
                    Id = Guid.CreateVersion7(),
                    OccurrenceId = occurrence.Id,
                    Timestamp = DateTime.UtcNow,
                    Level = "Error",
                    Message = $"Exception during dispatch: {ex.Message}",
                    Category = "Dispatcher"
                });

                failedToDispatchOccurrences.Add(occurrence);
            }
        });

        // Bulk lock release
        var jobIdsToUnlock = jobsToDispatch.Select(j => j.Id).ToList();
        var releasedCount = await _redisLock.ReleaseLocksBulkAsync(jobIdsToUnlock, Environment.MachineName, cancellationToken);
        _logger.Debug("Bulk lock release: {Released}/{Total} locks released", releasedCount, jobIdsToUnlock.Count);

        return failedToDispatchOccurrences;
    }

    /// <summary>
    /// Handles failed dispatch occurrences with exponential backoff retry scheduling.
    /// </summary>
    private async Task HandleFailedDispatchesAsync(
        AsyncServiceScope scope,
        List<ScheduledJob> jobsToDispatch,
        ConcurrentBag<JobOccurrence> failedToDispatchOccurrences,
        ConcurrentBag<JobOccurrenceLog> logsToInsert,
        CancellationToken cancellationToken)
    {
        if (failedToDispatchOccurrences.IsEmpty)
            return;

        try
        {
            await _redisStatsService.DecrementStatusCounterAsync(JobOccurrenceStatus.Queued, failedToDispatchOccurrences.Count, cancellationToken);
            _logger.Debug("Decremented Queued counter by {Count} for failed to dispatch jobs", failedToDispatchOccurrences.Count);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to decrement Queued counter (non-critical)");
        }

        foreach (var failedOccurrence in failedToDispatchOccurrences)
        {
            // Calculate next retry time with exponential backoff
            var retryDelaySeconds = Math.Min(30 * Math.Pow(2, failedOccurrence.DispatchRetryCount), 120);
            var nextRetryTime = DateTime.UtcNow.AddSeconds(retryDelaySeconds);

            failedOccurrence.NextDispatchRetryAt = nextRetryTime;
            failedOccurrence.DispatchRetryCount++;

            _logger.Debug("Occurrence {OccurrenceId} failed to dispatch (attempt {Attempt}). Next retry at {RetryTime} (in {Delay}s)",
                failedOccurrence.Id, failedOccurrence.DispatchRetryCount, nextRetryTime, retryDelaySeconds);

            logsToInsert.Add(new JobOccurrenceLog
            {
                Id = Guid.CreateVersion7(),
                OccurrenceId = failedOccurrence.Id,
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

        var jobOccurrenceRepository = scope.ServiceProvider.GetRequiredService<IMilvaionRepositoryBase<JobOccurrence>>();

        await jobOccurrenceRepository.BulkUpdateAsync([.. failedToDispatchOccurrences], (bc) =>
        {
            bc.PropertiesToInclude = bc.PropertiesToIncludeOnUpdate = _updatePropNames;
        }, cancellationToken: cancellationToken);

        _logger.Debug("Updated {Count} failed occurrences with retry schedule", failedToDispatchOccurrences.Count);
    }

    private bool CanDispatchBasedOnConcurrencyPolicy(HashSet<Guid> runningJobIdsSet, ScheduledJob job)
    {
        // Check ConcurrentExecutionPolicy
        var isRunning = runningJobIdsSet.Contains(job.Id);

        if (isRunning && job.ConcurrentExecutionPolicy == ConcurrentExecutionPolicy.Skip)
        {
            _logger.Information("Job {JobId} ({JobType}) skipped: Already running (Policy: Skip, Running in Redis: {IsRunning})", job.Id, job.JobNameInWorker, isRunning);

            // When job finishes, it will naturally become "due" again based on cron schedule
            return false;
        }

        return true;
    }

    private bool CanDispatchBasedOnWorkerCapacity(ScheduledJob job,
                                                  Dictionary<string, (int currentJobs, int? maxParallelJobs)> workerCapacities,
                                                  Dictionary<string, (int currentJobs, int? maxParallelJobs)> consumerCapacities)
    {
        if (string.IsNullOrWhiteSpace(job.WorkerId))
            return true;

        // Check if worker is active
        if (!workerCapacities.TryGetValue(job.WorkerId, out var workerCapacity))
        {
            _logger.Warning("Job {JobId} assigned to non-existent worker {WorkerId} (not in pre-fetch). Skipping.", job.Id, job.WorkerId);
            return false;
        }

        // Check if worker is inactive (-1 marker)
        if (workerCapacity.currentJobs == -1)
        {
            _logger.Debug("Job {JobId} assigned to inactive worker {WorkerId}. Skipping for this iteration.", job.Id, job.WorkerId);
            return false;
        }

        // Check worker-level capacity (total) - using pre-fetched data
        if (workerCapacity.maxParallelJobs.HasValue && workerCapacity.currentJobs >= workerCapacity.maxParallelJobs.Value)
        {
            _logger.Debug("Worker {WorkerId} at capacity ({CurrentJobs}/{MaxJobs}), job {JobId} skipped", job.WorkerId, workerCapacity.currentJobs, workerCapacity.maxParallelJobs.Value, job.Id);
            return false;
        }

        // Check consumer-level capacity (per job type, across all instances) - using pre-fetched data
        var consumerKey = $"{job.WorkerId}:{job.JobNameInWorker}";

        if (consumerCapacities.TryGetValue(consumerKey, out var consumerCapacity))
        {
            // MaxParallelJobsPerWorker: Global limit for this job type across all worker instances
            // If null, no global consumer limit (only per-instance limit applies)
            if (consumerCapacity.maxParallelJobs.HasValue && consumerCapacity.currentJobs >= consumerCapacity.maxParallelJobs.Value)
            {
                _logger.Debug("Consumer {WorkerId}/{JobType} at global capacity ({CurrentJobs}/{MaxJobsPerWorker}), job {JobId} skipped",
                    job.WorkerId, job.JobNameInWorker, consumerCapacity.currentJobs, consumerCapacity.maxParallelJobs.Value, job.Id);

                return false;
            }
        }

        _logger.Debug("Worker {WorkerId} capacity check passed ({CurrentJobs}/{MaxJobs})", job.WorkerId, workerCapacity.currentJobs, workerCapacity.maxParallelJobs?.ToString() ?? "unlimited");

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
                                            .Where(j => cacheMissIds.Contains(j.Id) && j.IsActive && !j.IsExternal)
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
        var allJobs = cachedJobs.Concat(dbJobs).Where(j => !j.Value.IsExternal).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        // 5.1. Populate ExecuteAt from ZSET for ALL jobs (since it's no longer in cache/DB)
        // Use bulk pipeline for efficiency (single Redis round-trip)
        var executeAtDict = await _redisScheduler.GetScheduledTimesBulkAsync(allJobs.Keys, cancellationToken);

        foreach (var (jobId, executeAt) in executeAtDict)
            if (executeAt.HasValue && allJobs.TryGetValue(jobId, out var job))
                job.ExecuteAt = executeAt.Value;

        return allJobs;
    }

    /// <summary>
    /// Dispatches a job to RabbitMQ (worker checks and lock already acquired).
    /// </summary>
    /// <returns>True if successfully published, false otherwise</returns>
    private async Task<bool> DispatchJobToRabbitMQAsync(ScheduledJob job, JobOccurrence occurrence, ConcurrentBag<JobOccurrenceLog> logsToInsert, CancellationToken cancellationToken)
    {
        try
        {
            // Publish to RabbitMQ with OccurrenceId (unique identifier for this execution)
            var published = await _rabbitMQPublisher.PublishJobAsync(job, occurrence.Id, cancellationToken);

            if (!published)
            {
                _logger.Error("Failed to publish job {JobId} to RabbitMQ", job.Id);

                occurrence.Status = JobOccurrenceStatus.Failed;
                occurrence.Exception = "Failed to publish to RabbitMQ";
                logsToInsert.Add(new JobOccurrenceLog
                {
                    Id = Guid.CreateVersion7(),
                    OccurrenceId = occurrence.Id,
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

            // Reschedule recurring job after successful publish
            await HandleRecurringJobAsync(job, cancellationToken);

            _logger.Debug("Job {JobId} ({JobType}) dispatched successfully with OccurrenceId {OccurrenceId}", job.Id, job.JobNameInWorker, occurrence.Id);

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
    /// Uses distributed lock to ensure only one instance performs recovery at a time (multi-instance safe).
    ///
    /// Sequential (blocks dispatcher start):
    ///   STEP 1 — Stale Redis cleanup (required before dispatcher polls Redis)
    ///   STEP 3 — Redis repopulation   (required before dispatcher polls Redis)
    ///
    /// Background (non-blocking):
    ///   STEP 2 — Zombie occurrence cleanup (DB-only, dispatcher does not depend on it)
    /// </summary>
    private async Task PerformStartupRecoveryAsync(CancellationToken cancellationToken)
    {
        const string recoveryLockName = "startup_recovery";
        var instanceId = Environment.MachineName;

        // Try to acquire distributed lock - only one instance should perform recovery
        var lockAcquired = await _redisLock.TryAcquireNamedLockAsync(
            recoveryLockName,
            instanceId,
            TimeSpan.FromMinutes(5), // TTL ensures lock is released even if instance crashes
            cancellationToken);

        if (!lockAcquired)
        {
            _logger.Information("Startup recovery skipped - another instance is already performing recovery. This instance will proceed without recovery.");
            return;
        }

        try
        {
            _logger.Information("Starting startup recovery (instance {InstanceId} acquired lock)...", instanceId);

            await using var scope = _serviceProvider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

            // NOTE: DeleteAllLocksAsync removed - it would delete locks from other running instances!
            // Each instance manages its own job locks during normal operation.

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

                        // Batch remove from scheduled set (single Redis call)
                        await _redisScheduler.RemoveFromScheduledSetBulkAsync(staleJobIds, cancellationToken);

                        // Batch remove cached jobs (single Redis pipeline)
                        await _redisScheduler.RemoveCachedJobsBulkAsync(staleJobIds, cancellationToken);

                        _logger.Information("Cleaned up {Count} stale Redis entries", staleJobIds.Count);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to clean up stale Redis data, continuing with recovery...");
            }

            // STEP 2: Clean up zombie occurrences — runs in the background so the dispatcher
            // can start immediately. This step is DB-only and the dispatcher does not depend on it.
            _ = Task.Run(() => CleanupZombieOccurrencesAsync(cancellationToken), cancellationToken);

            // STEP 3: Repopulate Redis with active jobs from database
            _logger.Information("Repopulating Redis with active jobs from database...");

            var activeJobs = await _getAllActiveJobsCompiled(dbContext).ToListAsync(cancellationToken);

            var addedCount = 0;
            var keptCount = 0;
            var cachedCount = 0;

            foreach (var job in activeJobs)
            {
                try
                {
                    // Check if job already exists in Redis
                    var existingScheduleTime = await _redisScheduler.GetScheduledTimeAsync(job.Id, cancellationToken);

                    // Redis holds the live schedule. Recovery only fills a gap - it never
                    // overwrites an existing score with the ExecuteAt column, which is
                    // frozen at the last create or update and is in the past for any
                    // recurring job that has run. Writing it back put every such job at a
                    // due score and made a restart fire the entire catalogue at once.
                    var scheduleTime = StartupScheduleResolver.Resolve(job.CronExpression, job.ExecuteAt, job.CompletedAt, existingScheduleTime, DateTime.UtcNow);

                    if (scheduleTime.HasValue)
                    {
                        var added = await _redisScheduler.AddToScheduledSetAsync(job.Id, scheduleTime.Value, cancellationToken);

                        if (added)
                            addedCount++;

                        _logger.Debug("Recovered schedule for job {JobId}: {ScheduleTime} (Cron: {CronExpression})", job.Id, scheduleTime.Value, job.CronExpression);
                    }
                    else if (existingScheduleTime.HasValue)
                    {
                        keptCount++;
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

            _logger.Information("Startup recovery (Redis) completed. ZSET: {Added} added, {Kept} already scheduled. Cache: {Cached} warmed. Total active jobs: {Total}. Zombie cleanup running in background.", addedCount, keptCount, cachedCount, activeJobs.Count);
        }
        finally
        {
            // Always release the lock when done (or on failure)
            await _redisLock.ReleaseNamedLockAsync(recoveryLockName, instanceId, cancellationToken);

            var redisStatsService = _serviceProvider.GetRequiredService<IRedisStatsService>();

            _ = Task.Run(async () =>
            {
                await using var syncScope = _serviceProvider.CreateAsyncScope();

                var dbContext = syncScope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

                await redisStatsService.SyncCountersFromDatabaseAsync(dbContext, cancellationToken);
            }, cancellationToken);

        }
    }

    /// <summary>
    /// Cleans up zombie occurrences (Queued/Running) left over from a previous run.
    /// Runs in the background after startup so it does not delay dispatcher start.
    /// </summary>
    private async Task CleanupZombieOccurrencesAsync(CancellationToken cancellationToken)
    {
        _logger.Information("Background zombie cleanup started...");

        try
        {
            await using var scope = _serviceProvider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

            var zombieStatuses = new[]
            {
                JobOccurrenceStatus.Queued,
                JobOccurrenceStatus.Running
            };

            // Grace period: Only mark as zombie if older than 2 minutes
            // This gives running jobs time to complete after restart
            var zombieThreshold = DateTime.UtcNow.AddMinutes(-2);

            const int zombieBatchSize = 500;
            var totalZombiesProcessed = 0;
            List<JobOccurrence> zombieOccurrences;

            do
            {
                zombieOccurrences = await dbContext.JobOccurrences
                                                   .AsNoTracking()
                                                   .Where(o => zombieStatuses.Contains(o.Status) && o.CreatedAt < zombieThreshold)
                                                   .OrderBy(o => o.CreatedAt)
                                                   .Take(zombieBatchSize)
                                                   .ToListAsync(cancellationToken);

                if (zombieOccurrences.Count > 0)
                {
                    _logger.Warning("Background zombie cleanup: processing {Count} occurrences (total so far: {Total})...", zombieOccurrences.Count, totalZombiesProcessed);

                    var zombieLogs = new List<JobOccurrenceLog>(zombieOccurrences.Count);

                    foreach (var occurrence in zombieOccurrences)
                    {
                        occurrence.Status = JobOccurrenceStatus.Failed;
                        occurrence.EndTime = DateTime.UtcNow;
                        occurrence.Exception = "System restart detected. Job was not completed before shutdown.";

                        zombieLogs.Add(new JobOccurrenceLog
                        {
                            Id = Guid.CreateVersion7(),
                            OccurrenceId = occurrence.Id,
                            Timestamp = DateTime.UtcNow,
                            Level = "Warning",
                            Message = "Job marked as failed due to system restart.",
                            Category = "StartupRecovery"
                        });
                    }

                    await dbContext.BulkUpdateAsync(zombieOccurrences, (bc) =>
                    {
                        bc.PropertiesToInclude = bc.PropertiesToIncludeOnUpdate = _startupRecoveryUpdatePropNames;
                    }, cancellationToken: cancellationToken);

                    if (zombieLogs.Count > 0)
                    {
                        var jobOccurrenceLogRepository = scope.ServiceProvider.GetRequiredService<IMilvaionRepositoryBase<JobOccurrenceLog>>();
                        await jobOccurrenceLogRepository.BulkAddAsync(zombieLogs, cancellationToken: cancellationToken);
                    }

                    totalZombiesProcessed += zombieOccurrences.Count;
                }
            } while (zombieOccurrences.Count == zombieBatchSize);

            if (totalZombiesProcessed > 0)
                _logger.Information("Background zombie cleanup completed. Cleaned up {Count} occurrences.", totalZombiesProcessed);
            else
                _logger.Information("Background zombie cleanup completed. No zombie occurrences found.");
        }
        catch (OperationCanceledException)
        {
            _logger.Warning("Background zombie cleanup cancelled (application shutting down).");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Background zombie cleanup failed.");
        }
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
        var retryLogs = new List<JobOccurrenceLog>();

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

                    retryLogs.Add(new JobOccurrenceLog
                    {
                        Id = Guid.CreateVersion7(),
                        OccurrenceId = occurrence.Id,
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

                        retryLogs.Add(new JobOccurrenceLog
                        {
                            Id = Guid.CreateVersion7(),
                            OccurrenceId = occurrence.Id,
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

                        retryLogs.Add(new JobOccurrenceLog
                        {
                            Id = Guid.CreateVersion7(),
                            OccurrenceId = occurrence.Id,
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

            // Bulk insert retry logs
            if (retryLogs.Count > 0)
            {
                var jobOccurrenceLogRepository = scope.ServiceProvider.GetRequiredService<IMilvaionRepositoryBase<JobOccurrenceLog>>();
                await jobOccurrenceLogRepository.BulkAddAsync(retryLogs, cancellationToken: cancellationToken);
                _logger.Debug("Bulk inserted {Count} retry logs", retryLogs.Count);
            }

            // Increment Redis Queued counter for successful retries (batch operation)
            if (successfulRetries.Count > 0)
            {
                try
                {
                    await _redisStatsService.IncrementStatusCounterAsync(JobOccurrenceStatus.Queued, successfulRetries.Count, cancellationToken);
                    _logger.Debug("Incremented Queued counter by {Count} for successful retries", successfulRetries.Count);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Failed to increment Queued counter for retries (non-critical)");
                }
            }

            _logger.Information("Dispatch retry completed: {Success} successful, {Failed} failed", successfulRetries.Count, failedRetries.Count);
        }
    }
}
