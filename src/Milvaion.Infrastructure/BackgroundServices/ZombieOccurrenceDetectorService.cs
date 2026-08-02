using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Dtos.AlertingDtos;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Interfaces.Redis;
using Milvaion.Infrastructure.BackgroundServices.Base;
using Milvaion.Infrastructure.Persistence.Context;
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
                await DetectAndCleanupProblematicOccurrencesAsync(stoppingToken);

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

        var allProblematicOccurrences = new List<JobOccurrence>();
        var logsToInsert = new List<JobOccurrenceLog>();

        // 1. Detect zombie Queued occurrences
        var (zombieQueued, zombieQueuedLogs) = await DetectZombieQueuedAsync(dbContext, cancellationToken);
        allProblematicOccurrences.AddRange(zombieQueued);
        logsToInsert.AddRange(zombieQueuedLogs);

        // 2. Detect lost Running occurrences
        var (lostRunning, lostRunningLogs) = await DetectLostRunningAsync(dbContext, cancellationToken);
        allProblematicOccurrences.AddRange(lostRunning);
        logsToInsert.AddRange(lostRunningLogs);

        if (allProblematicOccurrences.Count == 0)
        {
            _metrics.RecordZombieDetectionDuration(sw.Elapsed.TotalMilliseconds);
            return;
        }

        // Bulk update occurrences
        await dbContext.BulkUpdateAsync(allProblematicOccurrences, (bc) =>
        {
            bc.PropertiesToInclude = bc.PropertiesToIncludeOnUpdate = _updatePropNames;
        }, cancellationToken: cancellationToken);

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
        var queuedOccurrences = await dbContext.JobOccurrences.AsNoTracking()
                                               .Where(o => o.Status == JobOccurrenceStatus.Queued
                                                        && (o.WorkflowRunId == null || o.StepStatus != WorkflowStepStatus.Pending))
                                               .Select(JobOccurrence.Projections.DetectZombie)
                                               .ToListAsync(cancellationToken);

        if (queuedOccurrences.Count == 0)
            return ([], []);

        var zombieOccurrences = new List<JobOccurrence>();
        var zombieLogs = new List<JobOccurrenceLog>();

        foreach (var occurrence in queuedOccurrences)
        {
            var timeoutMinutes = occurrence.ZombieTimeoutMinutes ?? _options.ZombieTimeoutMinutes;

            if (occurrence.CreatedAt >= DateTime.UtcNow.AddMinutes(-timeoutMinutes))
                continue;

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

            await _redisScheduler.MarkJobAsCompletedAsync(occurrence.JobId, cancellationToken);

            // Update stats counters (Queued -> Unknown)
            _ = Task.Run(async () =>
            {
                try
                {
                    await _redisStatsService.UpdateStatusCountersAsync(JobOccurrenceStatus.Queued, JobOccurrenceStatus.Unknown, cancellationToken);
                }
                catch
                {
                    // Non-critical
                }
            }, CancellationToken.None);

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
        var heartbeatThreshold = DateTime.UtcNow.AddMinutes(-_options.ZombieTimeoutMinutes);

        var lostOccurrences = await dbContext.JobOccurrences.AsNoTracking()
                                             .Where(o => o.Status == JobOccurrenceStatus.Running && (o.LastHeartbeat == null || o.LastHeartbeat < heartbeatThreshold))
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
            occurrence.Exception = $"Job lost heartbeat after {_options.ZombieTimeoutMinutes}m. Worker status: {workerStatus}. Possible causes: Worker crashed, RabbitMQ connection lost, or network failure.";

            lostLogs.Add(new JobOccurrenceLog
            {
                Id = Guid.CreateVersion7(),
                OccurrenceId = occurrence.Id,
                Timestamp = DateTime.UtcNow,
                Level = "Warning",
                Message = $"Job marked as Unknown due to lost heartbeat (timeout: {_options.ZombieTimeoutMinutes}m)",
                Category = "ZombieDetector",
                Data = new Dictionary<string, object>
                {
                    ["WorkerStatus"] = workerStatus,
                    ["LastHeartbeat"] = occurrence.LastHeartbeat?.ToString("O") ?? "Never",
                    ["ThresholdMinutes"] = _options.ZombieTimeoutMinutes
                }
            });

            await _redisScheduler.MarkJobAsCompletedAsync(occurrence.JobId, cancellationToken);

            // Update stats counters (Running -> Unknown)
            _ = Task.Run(async () =>
            {
                try
                {
                    await _redisStatsService.UpdateStatusCountersAsync(JobOccurrenceStatus.Running, JobOccurrenceStatus.Unknown, cancellationToken);
                }
                catch
                {
                    // Non-critical
                }
            }, CancellationToken.None);

            _logger.Debug("Job {JobId} (Occurrence: {OccurrenceId}) marked as Unknown - no heartbeat since {LastHeartbeat}", occurrence.JobId, occurrence.Id, occurrence.LastHeartbeat?.ToString("O") ?? "never");
        }

        return (lostOccurrences, lostLogs);
    }
}