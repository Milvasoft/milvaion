using Milvasoft.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Dtos.AlertingDtos;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Interfaces.RabbitMQ;
using Milvaion.Application.Interfaces.Redis;
using Milvaion.Application.Interfaces.Secrets;
using Milvaion.Application.Utils.Constants;
using Milvaion.Infrastructure.BackgroundServices.Base;
using Milvaion.Infrastructure.Persistence;
using Milvaion.Infrastructure.Persistence.Context;
using Milvaion.Application.Utils.Scheduling;
using Milvaion.Domain;
using Milvaion.Infrastructure.Scheduling;
using Milvaion.Infrastructure.Services.Redis.Utils;
using Milvaion.Infrastructure.Telemetry;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Helpers;
using Milvasoft.Milvaion.Sdk.Utils;
using StackExchange.Redis;
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

    // Max dispatch attempts after the first publish failed (~6 minutes of retries in total).
    private const int _maxDispatchRetryAttempts = 5;
    private const string _dispatchRetryLockName = "dispatch_retry";

    // Unique per service instance: two pods can share a host name, and a lock must only ever be released by its real owner.
    private readonly string _instanceId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

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
                // The emergency stop is shared by every API instance, so pick up changes made through another one.
                await _controlService.RefreshAsync(stoppingToken);

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

                // Independent of the dispatch above: a cycle with no due jobs must still retry earlier publish failures.
                await RetryFailedDispatchesAsync(stoppingToken);

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

        // 0. Heartbeat. Done before anything else - including the early return below - so that only a real gap in
        //    polling, not an idle cycle, is ever recorded as an outage.
        var outage = await _redisScheduler.RecordDispatcherPollAsync(DateTime.UtcNow, MisfireRules.Grace, cancellationToken);

        // 1. Get due jobs from Redis ZSET
        var dueJobIds = await GetDueJobIdsFromRedisAsync(cancellationToken);

        if (dueJobIds.IsNullOrEmpty())
            return;

        _logger.Debug("Found {Count} due jobs in Redis", dueJobIds.Count);

        // 2. Fetch job details from cache/DB
        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();
        var allJobs = await GetDueJobsAsync(dbContext, dueJobIds, cancellationToken);
        var calendarsByJob = await LoadExclusionCalendarsAsync(dbContext, allJobs.Values, cancellationToken);

        // 3. Check running jobs and get capacities
        var runningJobIdsSet = await _redisScheduler.GetRunningJobIdsAsync(allJobs.Keys.ToList(), cancellationToken);
        var workerCapacities = await GetWorkerCapacitiesAsync(allJobs.Values, cancellationToken);
        var consumerCapacities = await GetConsumerCapacitiesAsync(allJobs.Values, cancellationToken);

        // 4. Validate and filter jobs
        var (jobsPassedValidation, toRemovedIdListScheduledSet) = ValidateAndFilterJobs(dueJobIds, allJobs, runningJobIdsSet, workerCapacities, consumerCapacities);

        _logger.Debug("Pre-validation: {Passed}/{Total} jobs passed", jobsPassedValidation.Count, dueJobIds.Count);

        // 4.1. Higher priority first. Everything below keeps this order, so it decides who gets a lock, an occurrence
        //      and a publish slot first; within a priority the job that has waited longest goes first.
        jobsPassedValidation = OrderByPriority(jobsPassedValidation);

        // 5. Acquire locks, decide each run (dispatch, catch up, or skip) and create occurrences
        var (occurrences, occurrencesAsEvents, logsToInsert, jobsToDispatch, lockedJobIds, decisions) = await AcquireLocksAndCreateOccurrencesAsync(jobsPassedValidation, outage, calendarsByJob, cancellationToken);

        try
        {
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

            // 8. Record metrics and update Redis counters. Skipped occurrences are final already and never queued.
            var queuedCount = occurrences.Count(o => o.Status == JobOccurrenceStatus.Queued);
            var skippedCount = occurrences.Count - queuedCount;

            RecordDispatchMetrics(sw, queuedCount);

            await UpdateRedisCountersAfterDispatchAsync(queuedCount, cancellationToken);

            if (skippedCount > 0)
                await UpdateSkippedCounterAsync(skippedCount, cancellationToken);

            // 9. Advance the schedule and dispatch to RabbitMQ
            var (retryOccurrences, abandonedOccurrences) = await DispatchJobsToRabbitMQAsync(jobsToDispatch, occurrences, decisions, logsToInsert, cancellationToken);

            // 10. Handle failed dispatches
            await HandleFailedDispatchesAsync(scope2, retryOccurrences, abandonedOccurrences, logsToInsert, cancellationToken);

            // 11. Mark one-time jobs as finished
            await MarkCompletedOneTimeJobsAsync(dbContext, jobsToDispatch, [.. retryOccurrences, .. abandonedOccurrences], cancellationToken);

            // 12. Insert logs
            var jobOccurrenceLogRepository = scope2.ServiceProvider.GetRequiredService<IMilvaionRepositoryBase<JobOccurrenceLog>>();

            if (!logsToInsert.IsEmpty)
            {
                await jobOccurrenceLogRepository.BulkAddAsync([.. logsToInsert], cancellationToken: cancellationToken);
                _logger.Debug("Bulk inserted {Count} logs from dispatch failures/retries", logsToInsert.Count);
            }
        }
        finally
        {
            // The only release path: also runs when persisting fails, so a job is not blocked for the whole lock TTL.
            if (lockedJobIds.Count > 0)
            {
                var releasedCount = await _redisLock.ReleaseLocksBulkAsync(lockedJobIds, _instanceId, CancellationToken.None);
                _logger.Debug("Bulk lock release: {Released}/{Total} locks released", releasedCount, lockedJobIds.Count);
            }
        }
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
                                                     List<JobOccurrence> failedToDispatchOccurrences,
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
        catch (Exception ex) when (ex is RedisCircuitBreakerOpenException or RedisException or TimeoutException)
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

        if (workerIds.Count == 0)
            return workerCapacities;

        // One pipelined read for all workers instead of two sequential reads per worker.
        var activeWorkers = (await _redisWorkerService.GetAllWorkersAsync(cancellationToken)).Where(w => w.Status == WorkerStatus.Active)
                                                                                             .ToDictionary(w => w.WorkerId);

        foreach (var workerId in workerIds)
        {
            // -1 marks the worker as inactive
            workerCapacities[workerId] = activeWorkers.TryGetValue(workerId, out var worker)
                ? (worker.Instances?.Sum(i => i.CurrentJobs) ?? 0, worker.MaxParallelJobs)
                : (-1, null);
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
    private async Task<(List<JobOccurrence> Occurrences, List<JobOccurrence> OccurrencesAsEvents, ConcurrentBag<JobOccurrenceLog> LogsToInsert, List<ScheduledJob> JobsToDispatch, List<Guid> LockedJobIds, Dictionary<Guid, RunDecision> Decisions)>
        AcquireLocksAndCreateOccurrencesAsync(List<ScheduledJob> jobsPassedValidation,
                                              DispatcherOutage outage,
                                              Dictionary<Guid, List<ExclusionCalendar>> calendarsByJob,
                                              CancellationToken cancellationToken)
    {
        var occurrences = new List<JobOccurrence>();
        var occurrencesAsEvents = new List<JobOccurrence>();
        var logsToInsert = new ConcurrentBag<JobOccurrenceLog>();
        var jobsToDispatch = new List<ScheduledJob>();
        var decisions = new Dictionary<Guid, RunDecision>();

        // Bulk lock -> Single Redis call using Lua script
        var jobIdsToLock = jobsPassedValidation.Select(j => j.Id).ToList();
        var lockResults = await _redisLock.TryAcquireLocksBulkAsync(jobIdsToLock, _instanceId, TimeSpan.FromSeconds(_options.LockTtlSeconds), cancellationToken);
        var lockedJobIds = lockResults.Where(l => l.Value).Select(l => l.Key).ToList();

        // Reading the score again under the lock is what makes the claim safe: the due list was read
        // without a lock, so another instance may have dispatched the job and released its lock since.
        var scoresUnderLock = lockedJobIds.Count > 0 ? await _redisScheduler.GetScheduledTimesBulkAsync(lockedJobIds, cancellationToken) : [];

        // Create occurrences only for jobs with acquired locks
        foreach (var job in jobsPassedValidation)
        {
            if (!lockResults.TryGetValue(job.Id, out var hasLock) || !hasLock)
                continue;

            if (!IsStillDueUnderLock(scoresUnderLock, job))
            {
                _logger.Debug("Job {JobId} was already handled by another instance. Skipping.", job.Id);
                continue;
            }

            // Decided under the lock, so two instances can never both record the same skip or catch-up.
            var now = DateTime.UtcNow;
            var decision = RunPlanner.Plan(job, now, outage, calendarsByJob.GetValueOrDefault(job.Id) ?? []);

            decisions[job.Id] = decision;

            // RunAll may drop the oldest missed runs, so the run actually sent can be later than the score.
            job.ExecuteAt = decision.RunSlotUtc;

            var correlationId = Guid.CreateVersion7();
            var status = decision.Dispatches || decision.Kind == RunDecisionKind.InvalidSchedule ? JobOccurrenceStatus.Queued : JobOccurrenceStatus.Skipped;

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
                Status = status,
                CreatedAt = now,
                CreatorUserName = GlobalConstant.SystemUsername
            };

            if (status == JobOccurrenceStatus.Skipped)
            {
                // Final at birth: no worker will ever pick it up, so it is closed here with the reason as its result.
                occurrence.EndTime = now;
                occurrence.Result = decision.Message;
                occurrence.StatusChangeLogs = [new OccurrenceStatusChangeLog { Timestamp = now, From = JobOccurrenceStatus.Queued, To = JobOccurrenceStatus.Skipped }];
            }

            occurrences.Add(occurrence);
            jobsToDispatch.Add(job);

            if (status == JobOccurrenceStatus.Queued)
            {
                logsToInsert.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    OccurrenceId = correlationId,
                    Timestamp = now,
                    Level = "Information",
                    Message = "Job dispatched to RabbitMQ queue and will start closely...",
                    Category = "Dispatcher",
                    Data = new Dictionary<string, object>
                    {
                        ["ExecuteAt"] = job.ExecuteAt.ToString("O"),
                        ["WorkerId"] = job.WorkerId,
                        ["JobVersion"] = job.Version,
                        ["Priority"] = job.Priority
                    }
                });
            }

            if (decision.Message is not null)
                logsToInsert.Add(NewSchedulerLog(correlationId, job, decision));

            occurrencesAsEvents.Add(new JobOccurrence
            {
                Id = correlationId,
                JobId = job.Id,
                JobName = job.DisplayName,
                JobDisplayName = job.DisplayName,
                Status = status,
                WorkerId = job.WorkerId,
                CreatedAt = now,
            });
        }

        return (occurrences, occurrencesAsEvents, logsToInsert, jobsToDispatch, lockedJobIds, decisions);
    }

    /// <summary>
    /// The log line that explains a catch-up run or a skipped run on its occurrence.
    /// </summary>
    private static JobOccurrenceLog NewSchedulerLog(Guid occurrenceId, ScheduledJob job, RunDecision decision)
    {
        var data = new Dictionary<string, object>
        {
            ["Decision"] = decision.Kind.ToString(),
            ["ScheduledFor"] = decision.RunSlotUtc.ToString("O"),
            ["LocalDate"] = decision.LocalDate.ToString("yyyy-MM-dd"),
            ["TimeZoneId"] = job.TimeZoneId ?? ScheduledJob.DefaultTimeZoneId,
            ["NextRunAt"] = decision.NextRunAtUtc?.ToString("O")
        };

        if (decision.MissedCount > 0)
        {
            data["CatchUp"] = decision.Kind == RunDecisionKind.CatchUp;
            data["MissedRunPolicy"] = job.MissedRunPolicy.ToString();
            data["MissedCount"] = decision.MissedCount;
            data["DroppedCount"] = decision.DroppedCount;
            data["FirstMissedAt"] = decision.FirstMissedUtc?.ToString("O");
            data["LastMissedAt"] = decision.LastMissedUtc?.ToString("O");
        }

        if (decision.ExcludedBy is not null)
        {
            data["ExclusionCalendarId"] = decision.ExcludedBy.Id;
            data["ExclusionCalendarName"] = decision.ExcludedBy.Name;
        }

        return new JobOccurrenceLog
        {
            Id = Guid.CreateVersion7(),
            OccurrenceId = occurrenceId,
            Timestamp = DateTime.UtcNow,
            Level = decision.Kind == RunDecisionKind.CatchUp ? "Warning" : "Information",
            Message = decision.Message,
            Category = "Scheduler",
            Data = data
        };
    }

    /// <summary>
    /// Sorts due jobs by priority, highest first, then by how long they have waited.
    /// </summary>
    internal static List<ScheduledJob> OrderByPriority(IEnumerable<ScheduledJob> jobs)
        => [.. jobs.OrderByDescending(j => Math.Clamp(j.Priority, ScheduledJob.MinPriority, ScheduledJob.MaxPriority)).ThenBy(j => j.ExecuteAt)];

    /// <summary>
    /// Loads the exclusion calendars referenced by the due jobs, in one query, keyed by job.
    /// </summary>
    private static async Task<Dictionary<Guid, List<ExclusionCalendar>>> LoadExclusionCalendarsAsync(MilvaionDbContext dbContext, IEnumerable<ScheduledJob> jobs, CancellationToken cancellationToken)
    {
        var jobsWithCalendars = jobs.Where(j => j.ExclusionCalendarIds?.Count > 0).ToList();

        if (jobsWithCalendars.Count == 0)
            return [];

        var calendarIds = jobsWithCalendars.SelectMany(j => j.ExclusionCalendarIds).Distinct().ToList();

        var calendars = await dbContext.ExclusionCalendars
                                       .AsNoTracking()
                                       .Where(c => calendarIds.Contains(c.Id))
                                       .ToDictionaryAsync(c => c.Id, cancellationToken);

        // A reference to a deleted calendar is ignored rather than failing the job.
        return jobsWithCalendars.ToDictionary(j => j.Id, j => j.ExclusionCalendarIds.Where(calendars.ContainsKey).Select(id => calendars[id]).ToList());
    }

    /// <summary>
    /// Counts skipped occurrences on the dashboard counters.
    /// </summary>
    private async Task UpdateSkippedCounterAsync(int count, CancellationToken cancellationToken)
    {
        try
        {
            await _redisStatsService.IncrementStatusCounterAsync(JobOccurrenceStatus.Skipped, count, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to increment Skipped counter (non-critical)");
        }
    }

    /// <summary>
    /// A job is still claimable only while its score is the one this cycle read as due.
    /// A missing score (removed, or Redis unavailable) means the job must not be dispatched in this cycle.
    /// </summary>
    private static bool IsStillDueUnderLock(Dictionary<Guid, DateTime?> scoresUnderLock, ScheduledJob job)
        => scoresUnderLock.TryGetValue(job.Id, out var score) && score.HasValue && score.Value == job.ExecuteAt;

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
                await _redisStatsService.TrackExecutionsAsync([.. occurrences.Where(o => o.Status == JobOccurrenceStatus.Queued).Select(o => o.Id)], cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to update stats counters (non-critical)");
            }

            var eventPublisher = scope.ServiceProvider.GetService<IJobOccurrenceEventPublisher>();
            await eventPublisher.PublishOccurrenceCreatedAsync(occurrencesAsEvents, _logger, cancellationToken);
        }
        catch (Exception ex) when (ex is DbUpdateException or Npgsql.PostgresException { SqlState: "23503" }) // BulkExtensions throws the Postgres exception as is
        {
            // Nothing was written. Continuing would publish occurrences that do not exist, so an unexplained failure stays a failure.
            if (!await RemoveJobsMissingFromDatabaseAsync(dbContext, jobOccurrenceRepository, allJobs, occurrences, cancellationToken))
                throw;
        }
    }

    /// <summary>
    /// Handles a failed insert by removing jobs that exist in Redis but no longer in the database, then inserting the rest.
    /// Without this a single stale entry fails the whole batch on every poll.
    /// </summary>
    /// <returns>False when no stale job explains the failure.</returns>
    private async Task<bool> RemoveJobsMissingFromDatabaseAsync(
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

            return true;
        }

        return false;
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
    /// <returns>
    /// Occurrences whose publish failed and that will be retried, and occurrences that were abandoned
    /// without being published because the schedule could not be advanced.
    /// </returns>
    private async Task<(List<JobOccurrence> Retry, List<JobOccurrence> Abandoned)> DispatchJobsToRabbitMQAsync(
        List<ScheduledJob> jobsToDispatch,
        List<JobOccurrence> occurrences,
        Dictionary<Guid, RunDecision> decisions,
        ConcurrentBag<JobOccurrenceLog> logsToInsert,
        CancellationToken cancellationToken)
    {
        var retry = new ConcurrentBag<JobOccurrence>();
        var abandoned = new ConcurrentBag<JobOccurrence>();
        var occurrenceByJobId = occurrences.ToDictionary(o => o.JobId);

        // A job may have been dropped while persisting (FK verification path).
        var dispatchable = jobsToDispatch.Where(j => occurrenceByJobId.ContainsKey(j.Id)).ToList();

        // One priority level at a time, highest first, so a lower priority message never reaches the broker ahead
        // of a higher priority one from the same cycle. Within a level publishes still run in parallel.
        foreach (var priorityGroup in dispatchable.GroupBy(j => Math.Clamp(j.Priority, ScheduledJob.MinPriority, ScheduledJob.MaxPriority)).OrderByDescending(g => g.Key))
        {
            await Parallel.ForEachAsync(priorityGroup, new ParallelOptions
            {
                MaxDegreeOfParallelism = 4,
                CancellationToken = cancellationToken
            }, async (job, ct) => await AdvanceAndPublishAsync(job, occurrenceByJobId[job.Id], decisions.GetValueOrDefault(job.Id), logsToInsert, retry, abandoned, ct));
        }

        return ([.. retry], [.. abandoned]);
    }

    /// <summary>
    /// Moves one job's schedule on and, unless its run was skipped, publishes it.
    /// </summary>
    private async Task AdvanceAndPublishAsync(ScheduledJob job,
                                              JobOccurrence occurrence,
                                              RunDecision decision,
                                              ConcurrentBag<JobOccurrenceLog> logsToInsert,
                                              ConcurrentBag<JobOccurrence> retry,
                                              ConcurrentBag<JobOccurrence> abandoned,
                                              CancellationToken ct)
    {
        // The schedule moves first. The other order - publish, then reschedule - sends the job
        // twice whenever the Redis write after a successful publish fails.
        if (!await TryAdvanceScheduleAsync(job, decision, ct))
        {
            occurrence.Status = JobOccurrenceStatus.Failed;
            occurrence.Exception = "The schedule could not be advanced in Redis, so this occurrence was not dispatched. The job stays due and will fire again.";

            logsToInsert.Add(NewDispatchLog(occurrence.Id, "Warning", occurrence.Exception));

            abandoned.Add(occurrence);

            return;
        }

        // Skipped by the missed-run policy or a calendar: the schedule has moved on and there is nothing to send.
        if (occurrence.Status == JobOccurrenceStatus.Skipped)
            return;

        try
        {
            if (await _rabbitMQPublisher.PublishJobAsync(job, occurrence.Id, ct))
            {
                _logger.Debug("Job {JobId} ({JobType}) dispatched successfully with OccurrenceId {OccurrenceId}", job.Id, job.JobNameInWorker, occurrence.Id);
                return;
            }

            _logger.Error("Failed to publish job {JobId} to RabbitMQ", job.Id);

            occurrence.Exception = "Failed to publish to RabbitMQ";
            logsToInsert.Add(NewDispatchLog(occurrence.Id, "Warning", "Failed to publish to RabbitMQ"));

            retry.Add(occurrence);
        }
        catch (SecretResolutionException ex)
        {
            // Retrying does not make a missing secret appear, and nothing was published.
            _logger.Warning("Occurrence {OccurrenceId} of job {JobId} failed before dispatch: {Reason}", occurrence.Id, job.Id, ex.Message);

            occurrence.Status = JobOccurrenceStatus.Failed;
            occurrence.Exception = ex.Message;
            logsToInsert.Add(NewDispatchLog(occurrence.Id, "Error", ex.Message));

            abandoned.Add(occurrence);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            occurrence.Exception = $"Dispatch exception: {ex.Message}";
            logsToInsert.Add(NewDispatchLog(occurrence.Id, "Error", $"Exception during dispatch: {ex.Message}"));

            retry.Add(occurrence);
        }
    }

    private static JobOccurrenceLog NewDispatchLog(Guid occurrenceId, string level, string message, Dictionary<string, object> data = null) => new()
    {
        Id = Guid.CreateVersion7(),
        OccurrenceId = occurrenceId,
        Timestamp = DateTime.UtcNow,
        Level = level,
        Message = message,
        Category = "Dispatcher",
        Data = data
    };

    /// <summary>
    /// Records the outcome of failed dispatches. Publish failures stay Queued with a retry time so that
    /// <see cref="RetryFailedDispatchesAsync"/> picks them up; the schedule has already moved on, so the
    /// retry is the only thing that re-sends this occurrence.
    /// </summary>
    private async Task HandleFailedDispatchesAsync(
        AsyncServiceScope scope,
        List<JobOccurrence> retryOccurrences,
        List<JobOccurrence> abandonedOccurrences,
        ConcurrentBag<JobOccurrenceLog> logsToInsert,
        CancellationToken cancellationToken)
    {
        if (retryOccurrences.Count == 0 && abandonedOccurrences.Count == 0)
            return;

        foreach (var failedOccurrence in retryOccurrences)
        {
            var (nextRetryTime, retryDelaySeconds) = ScheduleNextDispatchRetry(failedOccurrence);

            _logger.Debug("Occurrence {OccurrenceId} failed to dispatch (attempt {Attempt}). Next retry at {RetryTime} (in {Delay}s)",
                failedOccurrence.Id, failedOccurrence.DispatchRetryCount, nextRetryTime, retryDelaySeconds);

            logsToInsert.Add(NewDispatchLog(failedOccurrence.Id,
                                            "Warning",
                                            $"Dispatch failed (attempt {failedOccurrence.DispatchRetryCount}). Scheduled for retry at {nextRetryTime:HH:mm:ss}",
                                            new Dictionary<string, object>
                                            {
                                                ["RetryCount"] = failedOccurrence.DispatchRetryCount,
                                                ["NextRetryAt"] = nextRetryTime.ToString("O"),
                                                ["RetryDelaySeconds"] = retryDelaySeconds
                                            }));
        }

        await UpdateFailedStatusCountersAsync(abandonedOccurrences.Count, cancellationToken);

        var jobOccurrenceRepository = scope.ServiceProvider.GetRequiredService<IMilvaionRepositoryBase<JobOccurrence>>();

        await jobOccurrenceRepository.BulkUpdateAsync([.. retryOccurrences, .. abandonedOccurrences], (bc) =>
        {
            bc.PropertiesToInclude = bc.PropertiesToIncludeOnUpdate = _updatePropNames;
        }, cancellationToken: cancellationToken);

        _logger.Debug("Updated {Retry} occurrences with a retry schedule and {Abandoned} abandoned occurrences", retryOccurrences.Count, abandonedOccurrences.Count);
    }

    private static (DateTime NextRetryAt, double DelaySeconds) ScheduleNextDispatchRetry(JobOccurrence occurrence)
    {
        var retryDelaySeconds = Math.Min(30 * Math.Pow(2, occurrence.DispatchRetryCount), 120);
        var nextRetryTime = DateTime.UtcNow.AddSeconds(retryDelaySeconds);

        occurrence.NextDispatchRetryAt = nextRetryTime;
        occurrence.DispatchRetryCount++;

        return (nextRetryTime, retryDelaySeconds);
    }

    /// <summary>
    /// Moves occurrences counted as Queued to Failed on the dashboard counters.
    /// </summary>
    private async Task UpdateFailedStatusCountersAsync(int count, CancellationToken cancellationToken)
    {
        try
        {
            for (var i = 0; i < count; i++)
                await _redisStatsService.UpdateStatusCountersAsync(JobOccurrenceStatus.Queued, JobOccurrenceStatus.Failed, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to update status counters (non-critical)");
        }
    }

    private bool CanDispatchBasedOnConcurrencyPolicy(HashSet<Guid> runningJobIdsSet, ScheduledJob job)
    {
        // Check ConcurrentExecutionPolicy
        var isRunning = runningJobIdsSet.Contains(job.Id);

        if (isRunning && job.ConcurrentExecutionPolicy == ConcurrentExecutionPolicy.Skip)
        {
            _logger.Debug("Job {JobId} ({JobType}) skipped: Already running (Policy: Skip, Running in Redis: {IsRunning})", job.Id, job.JobNameInWorker, isRunning);

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
    /// Advances a job's schedule for this dispatch: a recurring job moves to its next slot, a one-time job
    /// leaves the scheduled set.
    /// </summary>
    /// <returns>False when Redis did not take the change, in which case the job must not be published.</returns>
    private async Task<bool> TryAdvanceScheduleAsync(ScheduledJob job, RunDecision decision, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(job.CronExpression))
            {
                var removed = await _redisScheduler.RemoveFromScheduledSetAsync(job.Id, cancellationToken);

                _logger.Debug("One-time job {JobId} removed from Redis scheduled set: {Removed}", job.Id, removed);

                return removed;
            }

            if (decision is null || decision.Kind == RunDecisionKind.InvalidSchedule)
            {
                _logger.Error("Failed to parse cron expression '{CronExpression}' for job {JobId}", job.CronExpression, job.Id);

                await _redisScheduler.RemoveFromScheduledSetAsync(job.Id, cancellationToken);
                await _redisScheduler.RemoveCachedJobAsync(job.Id, cancellationToken);

                return false;
            }

            // Worked out by RunPlanner in the job's time zone: the next cron run for a normal run, the next missed
            // run for a RunAll catch-up, or the first run on a non-excluded day after a calendar skip.
            var nextRun = decision.NextRunAtUtc;

            if (!nextRun.HasValue)
            {
                _logger.Warning("Cron expression {CronExpression} has no future occurrences, removing job {JobId}", job.CronExpression, job.Id);

                var removed = await _redisScheduler.RemoveFromScheduledSetAsync(job.Id, cancellationToken);
                await _redisScheduler.RemoveCachedJobAsync(job.Id, cancellationToken);

                return removed;
            }

            var updated = await _redisScheduler.UpdateScheduleAsync(job.Id, nextRun.Value, cancellationToken);

            _logger.Debug("Recurring job {JobId} rescheduled for {NextRun} (Cron: {CronExpression}): {Updated}", job.Id, nextRun.Value, job.CronExpression, updated);

            return updated;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to advance the schedule of job {JobId}", job.Id);

            return false;
        }
    }

    /// <summary>
    /// Performs startup recovery (zombie detection and Redis repopulation).
    /// Uses distributed lock to ensure only one instance performs recovery at a time (multi-instance safe).
    ///
    /// Sequential (blocks dispatcher start):
    ///   STEP 1 — Stale Redis cleanup (required before dispatcher polls Redis)
    ///   STEP 2 — Redis repopulation   (required before dispatcher polls Redis)
    ///
    /// Occurrences are deliberately left alone. Workers are separate processes, so an API restart says
    /// nothing about the jobs they are running; ZombieOccurrenceDetectorService judges those by heartbeat.
    /// </summary>
    private async Task PerformStartupRecoveryAsync(CancellationToken cancellationToken)
    {
        const string recoveryLockName = "startup_recovery";
        var instanceId = _instanceId;

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

            // STEP 2: Repopulate Redis with active jobs from database
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
                    var scheduleTime = StartupScheduleResolver.Resolve(job.CronExpression, job.ExecuteAt, job.CompletedAt, existingScheduleTime, DateTime.UtcNow, job.TimeZoneId);

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

            _logger.Information("Startup recovery (Redis) completed. ZSET: {Added} added, {Kept} already scheduled. Cache: {Cached} warmed. Total active jobs: {Total}.", addedCount, keptCount, cachedCount, activeJobs.Count);
        }
        finally
        {
            // Always release the lock when done (or on failure)
            await _redisLock.ReleaseNamedLockAsync(recoveryLockName, instanceId, cancellationToken);

            var redisStatsService = _serviceProvider.GetRequiredService<IRedisStatsService>();

            _ = Task.Run(async () =>
            {
                try
                {
                    await using var syncScope = _serviceProvider.CreateAsyncScope();

                    var dbContext = syncScope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

                    await redisStatsService.SyncCountersFromDatabaseAsync(dbContext, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Startup statistics sync failed (non-critical)");
                }
            }, CancellationToken.None);

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
                    // Throws until the migration that creates the table has run.
                    _ = await dbContext.JobOccurrences.AsNoTracking().AnyAsync(cancellationToken);

                    _logger.Information("Database migrations completed. JobOccurrences table is ready.");

                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
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
    /// Runs on one instance at a time; otherwise two instances would publish the same occurrence.
    /// </summary>
    private async Task RetryFailedDispatchesAsync(CancellationToken cancellationToken)
    {
        if (!await _redisLock.TryAcquireNamedLockAsync(_dispatchRetryLockName, _instanceId, TimeSpan.FromMinutes(2), cancellationToken))
            return;

        try
        {
            await RetryFailedDispatchesCoreAsync(cancellationToken);
        }
        finally
        {
            await _redisLock.ReleaseNamedLockAsync(_dispatchRetryLockName, _instanceId, CancellationToken.None);
        }
    }

    private async Task RetryFailedDispatchesCoreAsync(CancellationToken cancellationToken)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

        var retryOccurrences = await _getRetryOccurrencesCompiled(dbContext, DateTime.UtcNow, _maxDispatchRetryAttempts).ToListAsync(cancellationToken);

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

                MarkDispatchFailed(occurrence, "Job not found during retry");
                failedRetries.Add(occurrence);

                continue;
            }

            string failureReason;

            try
            {
                if (await _rabbitMQPublisher.PublishJobAsync(job, occurrence.CorrelationId, cancellationToken))
                {
                    retryLogs.Add(NewDispatchLog(occurrence.Id, "Information", $"Dispatch retry successful (attempt {occurrence.DispatchRetryCount})"));
                    successfulRetries.Add(occurrence);

                    _logger.Debug("Occurrence {OccurrenceId} successfully dispatched on retry attempt {Attempt}", occurrence.Id, occurrence.DispatchRetryCount);

                    continue;
                }

                failureReason = $"Failed to dispatch after {_maxDispatchRetryAttempts} attempts";
            }
            catch (SecretResolutionException ex)
            {
                MarkDispatchFailed(occurrence, ex.Message);
                retryLogs.Add(NewDispatchLog(occurrence.Id, "Error", ex.Message));
                failedRetries.Add(occurrence);

                continue;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Exception during dispatch retry for occurrence {OccurrenceId}", occurrence.Id);

                failureReason = $"Exception during retry: {ex.Message}";
            }

            if (occurrence.DispatchRetryCount >= _maxDispatchRetryAttempts - 1)
            {
                MarkDispatchFailed(occurrence, failureReason);

                retryLogs.Add(NewDispatchLog(occurrence.Id, "Error", $"Max dispatch retry attempts reached ({_maxDispatchRetryAttempts}). Marking as failed."));
            }
            else
            {
                var (nextRetryAt, _) = ScheduleNextDispatchRetry(occurrence);

                retryLogs.Add(NewDispatchLog(occurrence.Id, "Warning", $"Dispatch retry failed (attempt {occurrence.DispatchRetryCount}). Next retry at {nextRetryAt:HH:mm:ss}"));
            }

            failedRetries.Add(occurrence);
        }

        if (successfulRetries.Count > 0)
            await CompleteSuccessfulRetriesAsync(dbContext, successfulRetries, jobs, cancellationToken);

        if (failedRetries.Count > 0)
        {
            // A row that is no longer Queued is being handled by someone else (cancelled, or zombie-detected).
            await dbContext.BulkUpdateGuardedAsync(failedRetries,
                                                   (_, row) => row.Status == JobOccurrenceStatus.Queued,
                                                   bc => bc.PropertiesToInclude = bc.PropertiesToIncludeOnUpdate = _updatePropNames,
                                                   cancellationToken);

            await UpdateFailedStatusCountersAsync(failedRetries.Count(o => o.Status == JobOccurrenceStatus.Failed), cancellationToken);
        }

        if (retryLogs.Count > 0)
        {
            var jobOccurrenceLogRepository = scope.ServiceProvider.GetRequiredService<IMilvaionRepositoryBase<JobOccurrenceLog>>();
            await jobOccurrenceLogRepository.BulkAddAsync(retryLogs, cancellationToken: cancellationToken);
            _logger.Debug("Bulk inserted {Count} retry logs", retryLogs.Count);
        }

        _logger.Information("Dispatch retry completed: {Success} successful, {Failed} failed", successfulRetries.Count, failedRetries.Count);
    }

    private static void MarkDispatchFailed(JobOccurrence occurrence, string reason)
    {
        occurrence.Status = JobOccurrenceStatus.Failed;
        occurrence.Exception = reason;
        occurrence.NextDispatchRetryAt = null;
    }

    /// <summary>
    /// Clears the retry state of re-published occurrences and stamps one-time jobs as finished.
    /// Status is not written: the worker may already have reported Running by now.
    /// </summary>
    private static async Task CompleteSuccessfulRetriesAsync(MilvaionDbContext dbContext, List<JobOccurrence> successfulRetries, Dictionary<Guid, ScheduledJob> jobs, CancellationToken cancellationToken)
    {
        var occurrenceIds = successfulRetries.Select(o => o.Id).ToList();

        await dbContext.JobOccurrences
                       .Where(o => occurrenceIds.Contains(o.Id))
                       .ExecuteUpdateAsync(setters => setters.SetProperty(o => o.NextDispatchRetryAt, (DateTime?)null)
                                                             .SetProperty(o => o.Exception, o => o.Status == JobOccurrenceStatus.Queued ? null : o.Exception), cancellationToken);

        var oneTimeJobIds = successfulRetries.Select(o => jobs[o.JobId]).Where(j => string.IsNullOrWhiteSpace(j.CronExpression)).Select(j => j.Id).Distinct().ToList();

        if (oneTimeJobIds.Count == 0)
            return;

        var stampedAt = DateTime.UtcNow;

        await dbContext.ScheduledJobs
                       .Where(j => oneTimeJobIds.Contains(j.Id) && j.CompletedAt == null)
                       .ExecuteUpdateAsync(setters => setters.SetProperty(j => j.CompletedAt, stampedAt), cancellationToken);
    }
}
