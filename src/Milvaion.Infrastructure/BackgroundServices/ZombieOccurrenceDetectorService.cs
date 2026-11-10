using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Dtos.AlertingDtos;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Interfaces.Redis;
using Milvaion.Infrastructure.BackgroundServices.Base;
using Milvaion.Infrastructure.Persistence;
using Milvaion.Infrastructure.Persistence.Context;
using Milvaion.Infrastructure.Services.Redis;
using Milvaion.Infrastructure.Telemetry;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Utils;
using System.Diagnostics;

namespace Milvaion.Infrastructure.BackgroundServices;

/// <summary>
/// Background service that detects and cleans up zombie occurrences (Queued status for too long).
/// Prevents job blocking when occurrences are created but never consumed by workers.
/// </summary>
public class ZombieOccurrenceDetectorService(IServiceProvider serviceProvider,
                                             IRedisSchedulerService redisScheduler,
                                             IRedisWorkerService redisWorkerService,
                                             IRedisStatsService redisStatsService,
                                             IAlertNotifier alertNotifier,
                                             IOptions<ZombieOccurrenceDetectorOptions> options,
                                             ILoggerFactory loggerFactory,
                                             BackgroundServiceMetrics metrics,
                                             IMemoryStatsRegistry memoryStatsRegistry = null) : MemoryTrackedBackgroundService(loggerFactory, options.Value, memoryStatsRegistry)
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IRedisSchedulerService _redisScheduler = redisScheduler;
    private readonly IRedisWorkerService _redisWorkerService = redisWorkerService;
    private readonly IRedisStatsService _redisStatsService = redisStatsService;
    private readonly IAlertNotifier _alertNotifier = alertNotifier;
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<ZombieOccurrenceDetectorService>();
    private readonly ZombieOccurrenceDetectorOptions _options = options.Value;
    private readonly BackgroundServiceMetrics _metrics = metrics;
    private readonly string _instanceId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private const string _lockName = "zombie_detector";
    private static readonly TimeSpan _lockTtl = TimeSpan.FromSeconds(30);

    // Upper bound per pass: a large backlog is worked through over several passes instead of loaded at once.
    private const int _maxOccurrencesPerPass = 5000;

    private readonly static List<string> _updatePropNames =
    [
        nameof(JobOccurrence.Status),
        nameof(JobOccurrence.StatusChangeLogs),
        nameof(JobOccurrence.Exception),
        nameof(JobOccurrence.EndTime),
        nameof(JobOccurrence.DurationMs)
    ];

    /// <inheritdoc/>
    protected override string ServiceName => "ZombieOccurrenceDetector";

    /// <inheritdoc />
    protected override async Task ExecuteWithMemoryTrackingAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.Warning("Zombie occurrence detection is disabled. Skipping startup.");
            return;
        }

        _logger.Information("Zombie occurrence detection started. Interval: {Interval}s, ZombieTimeout: {Timeout}m", _options.CheckIntervalSeconds, _options.ZombieTimeoutMinutes);

        _logger.Debug("Starting. Memory: {Memory} MB", GC.GetTotalMemory(false) / 1024 / 1024);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // One instance per pass, or each would report and count the same zombies.
                await _serviceProvider.GetRequiredService<IRedisLockService>()
                                      .RunExclusiveAsync(_lockName, _instanceId, _lockTtl, DetectAndCleanupProblematicOccurrencesAsync, _logger, stoppingToken);

                TrackMemoryAfterIteration();
            }
            catch (OperationCanceledException)
            {
                _logger.Information("Zombie occurrence detection shutting down gracefully");
                break;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error during zombie occurrence detection");
            }

            // Wait before next check
            await Task.Delay(TimeSpan.FromSeconds(_options.CheckIntervalSeconds), stoppingToken);
        }

        _logger.Information("Zombie occurrence detection stopped");
    }

    /// <summary>
    /// Detects and cleans up problematic occurrences (zombie queued + lost running).
    /// </summary>
    private async Task DetectAndCleanupProblematicOccurrencesAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

        // Drop orphaned "running" flags from Redis first, so a job stuck being skipped
        // (marked running but with no active occurrence) can run again.
        await ReconcileRunningJobsAsync(dbContext, cancellationToken);

        // 1. Detect zombie Queued occurrences
        var (zombieQueued, zombieQueuedLogs) = await DetectZombieQueuedAsync(dbContext, cancellationToken);

        // 2. Detect lost Running occurrences
        var (lostRunning, lostRunningLogs) = await DetectLostRunningAsync(dbContext, cancellationToken);

        if (zombieQueued.Count == 0 && lostRunning.Count == 0)
        {
            _metrics.RecordZombieDetectionDuration(sw.Elapsed.TotalMilliseconds);
            return;
        }

        // The checks above ran on a snapshot and the worker may have reported since. Only rows that are still stuck
        // are written, so a Completed that arrived in the meantime is not replaced by Unknown.
        var rejected = await dbContext.BulkUpdateGuardedAsync(zombieQueued,
                                                              (_, row) => row.Status == JobOccurrenceStatus.Queued,
                                                              ConfigureBulkUpdate,
                                                              cancellationToken);

        rejected.AddRange(await dbContext.BulkUpdateGuardedAsync(lostRunning,
                                                                 (occurrence, row) => row.Status == JobOccurrenceStatus.Running && IsHeartbeatExpired(row.LastHeartbeat, TimeoutFor(occurrence)),
                                                                 ConfigureBulkUpdate,
                                                                 cancellationToken));

        var rejectedIds = rejected.Select(o => o.Id).ToHashSet();

        zombieQueued.RemoveAll(o => rejectedIds.Contains(o.Id));
        lostRunning.RemoveAll(o => rejectedIds.Contains(o.Id));

        var allProblematicOccurrences = zombieQueued.Concat(lostRunning).ToList();
        var logsToInsert = zombieQueuedLogs.Concat(lostRunningLogs).Where(l => !rejectedIds.Contains(l.OccurrenceId)).ToList();

        if (allProblematicOccurrences.Count == 0)
        {
            _metrics.RecordZombieDetectionDuration(sw.Elapsed.TotalMilliseconds);
            return;
        }

        await _redisScheduler.RemoveRunningJobsAsync(allProblematicOccurrences.Select(o => o.JobId).Distinct().ToList(), cancellationToken);

        await UpdateStatusCountersAsync(JobOccurrenceStatus.Queued, zombieQueued.Count, cancellationToken);
        await UpdateStatusCountersAsync(JobOccurrenceStatus.Running, lostRunning.Count, cancellationToken);

        // Bulk insert logs
        if (logsToInsert.Count > 0)
        {
            var jobOccurrenceLogRepository = scope.ServiceProvider.GetRequiredService<IMilvaionRepositoryBase<JobOccurrenceLog>>();
            await jobOccurrenceLogRepository.BulkAddAsync(logsToInsert, cancellationToken: cancellationToken);
            _logger.Debug("Bulk inserted {Count} zombie/lost occurrence logs", logsToInsert.Count);
        }

        // Publish status change events
        var eventPublisher = scope.ServiceProvider.GetService<IJobOccurrenceEventPublisher>();

        await eventPublisher.PublishOccurrenceUpdatedAsync(allProblematicOccurrences, _logger, cancellationToken);

        // Record metrics
        _metrics.RecordZombiesDetected(allProblematicOccurrences.Count);
        _metrics.RecordZombiesRecovered(allProblematicOccurrences.Count);
        _metrics.RecordZombieDetectionDuration(sw.Elapsed.TotalMilliseconds);

        _logger.Debug("Cleaned up {ZombieCount} zombie + {LostCount} lost = {TotalCount} problematic occurrences",
            zombieQueued.Count, lostRunning.Count, allProblematicOccurrences.Count);

        // Send zombie occurrence alerts
        _alertNotifier.SendFireAndForget(AlertType.ZombieOccurrenceDetected, new AlertPayload
        {
            Title = "Zombie Occurrences Detected",
            Message = $"Detected and cleaned up {allProblematicOccurrences.Count} problematic occurrences ({zombieQueued.Count} zombie queued, {lostRunning.Count} lost running).",
            Severity = AlertSeverity.Warning,
            Source = nameof(ZombieOccurrenceDetectorService),
            ThreadKey = "zombie-occurrence-detected",
            ActionLink = "/executions",
            AdditionalData = new
            {
                ZombieQueuedCount = zombieQueued.Count,
                LostRunningCount = lostRunning.Count,
                TotalCount = allProblematicOccurrences.Count,
                OccurrenceIds = allProblematicOccurrences.Select(o => o.Id).Take(10).ToList()
            }
        });
    }

    private static void ConfigureBulkUpdate(EFCore.BulkExtensions.BulkConfig config) => config.PropertiesToInclude = config.PropertiesToIncludeOnUpdate = _updatePropNames;

    private int TimeoutFor(JobOccurrence occurrence) => occurrence.ZombieTimeoutMinutes ?? _options.ZombieTimeoutMinutes;

    private static bool IsHeartbeatExpired(DateTime? lastHeartbeat, int timeoutMinutes) => lastHeartbeat == null || lastHeartbeat < DateTime.UtcNow.AddMinutes(-timeoutMinutes);

    private async Task UpdateStatusCountersAsync(JobOccurrenceStatus from, int count, CancellationToken cancellationToken)
    {
        try
        {
            for (var i = 0; i < count; i++)
                await _redisStatsService.UpdateStatusCountersAsync(from, JobOccurrenceStatus.Unknown, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to update status counters (non-critical)");
        }
    }

    /// <summary>
    /// A job whose most recent occurrence was created within this window is treated as possibly
    /// still mid-dispatch and is never reconciled away, so we don't race a job that was just
    /// marked running but whose occurrence has not been persisted yet.
    /// </summary>
    private const int _runningJobReconcileGraceMinutes = 10;

    /// <summary>
    /// Removes orphaned "running" flags from Redis: job IDs marked running that have no active
    /// (Queued/Running) occurrence in the database. This happens when a completion's removal was
    /// missed - a Redis blip, an open circuit breaker, or a worker crash - leaving the job flagged
    /// running forever so the dispatcher keeps skipping it ("Policy: Skip, Running in Redis: True").
    /// The database-side zombie detection never sees these because there is no active occurrence to
    /// find; reconciling the set is the only thing that frees them.
    /// </summary>
    private async Task ReconcileRunningJobsAsync(MilvaionDbContext dbContext, CancellationToken cancellationToken)
    {
        var runningInRedis = await _redisScheduler.GetAllRunningJobIdsAsync(cancellationToken);

        if (runningInRedis.Count == 0)
            return;

        var ids = runningInRedis.ToList();
        var graceCutoff = DateTime.UtcNow.AddMinutes(-_runningJobReconcileGraceMinutes);

        // Jobs that are legitimately busy: an active occurrence, or one created so recently it may
        // still be mid-dispatch.
        var keep = await dbContext.JobOccurrences.AsNoTracking()
                                  .Where(o => ids.Contains(o.JobId)
                                           && (o.Status == JobOccurrenceStatus.Queued
                                            || o.Status == JobOccurrenceStatus.Running
                                            || o.CreatedAt > graceCutoff))
                                  .Select(o => o.JobId)
                                  .Distinct()
                                  .ToListAsync(cancellationToken);

        var orphans = runningInRedis.Except(keep).ToList();

        if (orphans.Count == 0)
            return;

        var removed = await _redisScheduler.RemoveRunningJobsAsync(orphans, cancellationToken);

        _logger.Warning("Reconciled {Removed} orphaned running-job flag(s) from Redis (marked running but no active occurrence). Affected jobs were being skipped and can run again.", removed);
    }

    /// <summary>
    /// Detects Queued occurrences that exceeded timeout and marks them as Unknown.
    /// </summary>
    private async Task<(List<JobOccurrence> occurrences, List<JobOccurrenceLog> logs)> DetectZombieQueuedAsync(MilvaionDbContext dbContext, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var defaultTimeout = _options.ZombieTimeoutMinutes;

        // An occurrence waiting for a dispatch retry has no message in the queue yet; the dispatcher owns it until the retries run out.
        var queuedOccurrences = await dbContext.JobOccurrences.AsNoTracking()
                                               .Where(o => o.Status == JobOccurrenceStatus.Queued
                                                        && o.NextDispatchRetryAt == null
                                                        && (o.WorkflowRunId == null || o.StepStatus != WorkflowStepStatus.Pending)
                                                        && o.CreatedAt.AddMinutes(o.ZombieTimeoutMinutes ?? defaultTimeout) < now)
                                               .OrderBy(o => o.CreatedAt)
                                               .Take(_maxOccurrencesPerPass)
                                               .Select(JobOccurrence.Projections.DetectZombie)
                                               .ToListAsync(cancellationToken);

        if (queuedOccurrences.Count == 0)
            return ([], []);

        var zombieOccurrences = new List<JobOccurrence>();
        var zombieLogs = new List<JobOccurrenceLog>();

        foreach (var occurrence in queuedOccurrences)
        {
            var timeoutMinutes = occurrence.ZombieTimeoutMinutes ?? defaultTimeout;

            var stuckDuration = (DateTime.UtcNow - occurrence.CreatedAt).TotalMinutes;

            occurrence.StatusChangeLogs ??= [];
            occurrence.StatusChangeLogs.Add(new OccurrenceStatusChangeLog
            {
                Timestamp = DateTime.UtcNow,
                From = JobOccurrenceStatus.Queued,
                To = JobOccurrenceStatus.Unknown
            });

            occurrence.Status = JobOccurrenceStatus.Unknown;
            occurrence.Exception = $"Zombie occurrence detected - stuck in Queued status for {stuckDuration:F1} minutes (timeout: {timeoutMinutes} minutes). Likely causes: Worker never consumed the message, RabbitMQ queue issue, or worker was down during dispatch.";
            occurrence.EndTime = DateTime.UtcNow;
            occurrence.DurationMs = (long)(DateTime.UtcNow - occurrence.CreatedAt).TotalMilliseconds;

            zombieLogs.Add(new JobOccurrenceLog
            {
                Id = Guid.CreateVersion7(),
                OccurrenceId = occurrence.Id,
                Timestamp = DateTime.UtcNow,
                Level = "Error",
                Message = $"Zombie occurrence detected and marked as Unknown (stuck in Queued status since {occurrence.CreatedAt:HH:mm:ss})",
                Category = "ZombieDetector",
                Data = new Dictionary<string, object>
                {
                    ["CreatedAt"] = occurrence.CreatedAt.ToString("O"),
                    ["TimeoutMinutes"] = timeoutMinutes,
                    ["JobSpecificTimeout"] = occurrence.ZombieTimeoutMinutes.HasValue,
                    ["StuckDuration"] = $"{stuckDuration:F1}m"
                }
            });

            zombieOccurrences.Add(occurrence);

            _logger.Debug("Zombie occurrence {OccurrenceId} (Job: {JobId}) marked as Unknown - stuck for {Duration:F1}m", occurrence.Id, occurrence.JobId, stuckDuration);
        }

        return (zombieOccurrences, zombieLogs);
    }

    /// <summary>
    /// Detects Running occurrences that lost heartbeat and marks them as Unknown.
    /// </summary>
    private async Task<(List<JobOccurrence> occurrences, List<JobOccurrenceLog> logs)> DetectLostRunningAsync(MilvaionDbContext dbContext, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var defaultTimeout = _options.ZombieTimeoutMinutes;

        var lostOccurrences = await dbContext.JobOccurrences.AsNoTracking()
                                             .Where(o => o.Status == JobOccurrenceStatus.Running
                                                      && (o.LastHeartbeat == null || o.LastHeartbeat.Value.AddMinutes(o.ZombieTimeoutMinutes ?? defaultTimeout) < now))
                                             .OrderBy(o => o.LastHeartbeat)
                                             .Take(_maxOccurrencesPerPass)
                                             .Select(JobOccurrence.Projections.RecoverLostJob)
                                             .ToListAsync(cancellationToken);

        if (lostOccurrences.Count == 0)
            return ([], []);

        var lostLogs = new List<JobOccurrenceLog>();

        foreach (var occurrence in lostOccurrences)
        {
            var worker = await _redisWorkerService.GetWorkerAsync(occurrence.WorkerId, cancellationToken);
            var workerStatus = worker?.Status.ToString() ?? "NotFound";

            occurrence.StatusChangeLogs ??= [];
            occurrence.StatusChangeLogs.Add(new OccurrenceStatusChangeLog
            {
                Timestamp = DateTime.UtcNow,
                From = JobOccurrenceStatus.Running,
                To = JobOccurrenceStatus.Unknown
            });

            occurrence.Status = JobOccurrenceStatus.Unknown;
            occurrence.EndTime = DateTime.UtcNow;
            occurrence.DurationMs = occurrence.StartTime.HasValue ? (long)(DateTime.UtcNow - occurrence.StartTime.Value).TotalMilliseconds : null;
            occurrence.Exception = $"Job lost heartbeat after {TimeoutFor(occurrence)}m. Worker status: {workerStatus}. Possible causes: Worker crashed, RabbitMQ connection lost, or network failure.";

            lostLogs.Add(new JobOccurrenceLog
            {
                Id = Guid.CreateVersion7(),
                OccurrenceId = occurrence.Id,
                Timestamp = DateTime.UtcNow,
                Level = "Warning",
                Message = $"Job marked as Unknown due to lost heartbeat (timeout: {TimeoutFor(occurrence)}m)",
                Category = "ZombieDetector",
                Data = new Dictionary<string, object>
                {
                    ["WorkerStatus"] = workerStatus,
                    ["LastHeartbeat"] = occurrence.LastHeartbeat?.ToString("O") ?? "Never",
                    ["ThresholdMinutes"] = TimeoutFor(occurrence)
                }
            });

            _logger.Debug("Job {JobId} (Occurrence: {OccurrenceId}) marked as Unknown - no heartbeat since {LastHeartbeat}", occurrence.JobId, occurrence.Id, occurrence.LastHeartbeat?.ToString("O") ?? "never");
        }

        return (lostOccurrences, lostLogs);
    }
}