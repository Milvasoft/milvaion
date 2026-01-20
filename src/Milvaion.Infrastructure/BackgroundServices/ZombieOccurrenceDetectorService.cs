using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Interfaces.Redis;
using Milvaion.Infrastructure.BackgroundServices.Base;
using Milvaion.Infrastructure.Persistence.Context;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Utils;

namespace Milvaion.Infrastructure.BackgroundServices;

/// <summary>
/// Background service that detects and cleans up zombie occurrences (Queued status for too long).
/// Prevents job blocking when occurrences are created but never consumed by workers.
/// </summary>
public class ZombieOccurrenceDetectorService(IServiceProvider serviceProvider,
                                             IRedisSchedulerService redisScheduler,
                                             IRedisWorkerService redisWorkerService,
                                             IOptions<ZombieOccurrenceDetectorOptions> options,
                                             ILoggerFactory loggerFactory,
                                             IMemoryStatsRegistry memoryStatsRegistry = null) : MemoryTrackedBackgroundService(loggerFactory, options.Value, memoryStatsRegistry)
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IRedisSchedulerService _redisScheduler = redisScheduler;
    private readonly IRedisWorkerService _redisWorkerService = redisWorkerService;
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<ZombieOccurrenceDetectorService>();
    private readonly ZombieOccurrenceDetectorOptions _options = options.Value;

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

    private readonly static List<string> _updatePropNames =
    [
        nameof(JobOccurrence.Status),
        nameof(JobOccurrence.StatusChangeLogs),
        nameof(JobOccurrence.Logs),
        nameof(JobOccurrence.Exception),
        nameof(JobOccurrence.EndTime),
        nameof(JobOccurrence.DurationMs)
    ];

    /// <summary>
    /// Detects and cleans up problematic occurrences (zombie queued + lost running).
    /// </summary>
    private async Task DetectAndCleanupProblematicOccurrencesAsync(CancellationToken cancellationToken)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

        var allProblematicOccurrences = new List<JobOccurrence>();

        // 1. Detect zombie Queued occurrences
        var zombieQueued = await DetectZombieQueuedAsync(dbContext, cancellationToken);
        allProblematicOccurrences.AddRange(zombieQueued);

        // 2. Detect lost Running occurrences
        var lostRunning = await DetectLostRunningAsync(dbContext, cancellationToken);
        allProblematicOccurrences.AddRange(lostRunning);

        if (allProblematicOccurrences.Count == 0)
            return;

        // Single bulk update for all
        await dbContext.BulkUpdateAsync(allProblematicOccurrences, (bc) =>
        {
            bc.PropertiesToInclude = bc.PropertiesToIncludeOnUpdate = _updatePropNames;
        }, cancellationToken: cancellationToken);

        // Single event publish for all
        var eventPublisher = scope.ServiceProvider.GetService<IJobOccurrenceEventPublisher>();
        await eventPublisher.PublishOccurrenceUpdatedAsync(allProblematicOccurrences, _logger, cancellationToken);

        _logger.Debug("Cleaned up {ZombieCount} zombie + {LostCount} lost = {TotalCount} problematic occurrences",
            zombieQueued.Count, lostRunning.Count, allProblematicOccurrences.Count);
    }

    /// <summary>
    /// Detects Queued occurrences that exceeded timeout and marks them as Unknown.
    /// </summary>
    private async Task<List<JobOccurrence>> DetectZombieQueuedAsync(MilvaionDbContext dbContext, CancellationToken cancellationToken)
    {
        var queuedOccurrences = await dbContext.JobOccurrences
                                               .Where(o => o.Status == JobOccurrenceStatus.Queued)
                                               .Select(JobOccurrence.Projections.DetectZombie)
                                               .ToListAsync(cancellationToken);

        if (queuedOccurrences.Count == 0)
            return [];

        var zombieOccurrences = new List<JobOccurrence>();

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
            occurrence.Logs.Add(new OccurrenceLog
            {
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

            zombieOccurrences.Add(occurrence);

            _logger.Debug("Zombie occurrence {OccurrenceId} (Job: {JobId}) marked as Unknown - stuck for {Duration:F1}m",
                occurrence.Id, occurrence.JobId, stuckDuration);
        }

        return zombieOccurrences;
    }

    /// <summary>
    /// Detects Running occurrences that lost heartbeat and marks them as Unknown.
    /// </summary>
    private async Task<List<JobOccurrence>> DetectLostRunningAsync(MilvaionDbContext dbContext, CancellationToken cancellationToken)
    {
        var heartbeatThreshold = DateTime.UtcNow.AddMinutes(-_options.ZombieTimeoutMinutes);

        var lostOccurrences = await dbContext.JobOccurrences
                                             .Where(o => o.Status == JobOccurrenceStatus.Running && (o.LastHeartbeat == null || o.LastHeartbeat < heartbeatThreshold))
                                             .Select(JobOccurrence.Projections.RecoverLostJob)
                                             .ToListAsync(cancellationToken);

        if (lostOccurrences.Count == 0)
            return [];

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
            occurrence.Logs.Add(new OccurrenceLog
            {
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

            _logger.Debug("Job {JobId} (Occurrence: {OccurrenceId}) marked as Unknown - no heartbeat since {LastHeartbeat}", occurrence.JobId, occurrence.Id, occurrence.LastHeartbeat?.ToString("O") ?? "never");
        }

        return lostOccurrences;
    }
}