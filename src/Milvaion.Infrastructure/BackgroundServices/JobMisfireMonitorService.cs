using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Milvaion.Application.Dtos.AlertingDtos;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Interfaces.Redis;
using Milvaion.Infrastructure.Persistence.Context;
using StackExchange.Redis;

namespace Milvaion.Infrastructure.BackgroundServices;

/// <summary>
/// Detects misfired jobs and raises <see cref="AlertType.JobMisfired"/>.
///
/// A due job lives in the Redis scheduled set with its intended fire time as the score; the
/// dispatcher polls that set every few seconds and clears each job as it dispatches it. So a job
/// whose scheduled time is more than the grace window in the past yet is still sitting in the set
/// was never dispatched on time - the dispatcher was down, stalled, or could not place it. That
/// is the misfire.
///
/// De-duplicated through Redis per (job, scheduled slot), so one missed run alerts once. This is a
/// separate, silent monitor rather than a hook in the dispatcher's hot path: it never touches the
/// dispatch flow, is not part of any monitoring/metrics registry, and swallows its own failures.
/// </summary>
public class JobMisfireMonitorService(IServiceScopeFactory scopeFactory,
                                      IRedisSchedulerService redisScheduler,
                                      IAlertNotifier alertNotifier,
                                      IConnectionMultiplexer redis,
                                      ILogger<JobMisfireMonitorService> logger) : BackgroundService
{
    /// <summary>How late a still-scheduled job must be before it counts as a misfire.</summary>
    private const int _misfireGraceSeconds = 60;

    private static readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _dedupTtl = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IRedisSchedulerService _redisScheduler = redisScheduler;
    private readonly IAlertNotifier _alertNotifier = alertNotifier;
    private readonly IConnectionMultiplexer _redis = redis;
    private readonly ILogger<JobMisfireMonitorService> _logger = logger;

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the dispatcher settle before the first scan, so a brief startup lag is not a misfire.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Quiet by design: never surfaces to the user or disturbs the dispatcher.
                _logger.LogDebug(ex, "Job misfire check failed.");
            }

            try
            {
                await Task.Delay(_checkInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var cutoff = now.AddSeconds(-_misfireGraceSeconds);

        // Jobs whose intended fire time is older than the grace window but are still queued.
        var overdue = await _redisScheduler.GetScheduledJobsInRangeAsync(DateTime.UnixEpoch, cutoff, 200, cancellationToken);

        if (overdue.Count == 0)
            return;

        var db = _redis.GetDatabase();

        var ids = overdue.Select(o => o.Key).ToList();

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

        var names = await dbContext.ScheduledJobs
            .AsNoTracking()
            .Where(j => ids.Contains(j.Id))
            .Select(j => new { j.Id, j.DisplayName })
            .ToDictionaryAsync(j => j.Id, cancellationToken);

        foreach (var (jobId, scheduledAt) in overdue)
        {
            var scheduledEpoch = new DateTimeOffset(scheduledAt, TimeSpan.Zero).ToUnixTimeSeconds();

            // One alert per missed slot, across all instances.
            var firstTime = await db.StringSetAsync($"milvaion:misfire-alert:{jobId}:{scheduledEpoch}", "1", _dedupTtl, When.NotExists);
            if (!firstTime)
                continue;

            names.TryGetValue(jobId, out var name);
            var display = string.IsNullOrWhiteSpace(name?.DisplayName) ? jobId.ToString() : name.DisplayName;
            var lateSeconds = (int)(now - scheduledAt).TotalSeconds;

            _alertNotifier.SendFireAndForget(AlertType.JobMisfired, new AlertPayload
            {
                Title = "Job misfired",
                Message = $"Job '{display}' missed its scheduled run at {scheduledAt:yyyy-MM-dd HH:mm:ss} UTC (late by {lateSeconds}s).",
                Severity = AlertSeverity.Warning,
                Source = "JobMisfireMonitor",
                ActionLink = $"/jobs/{jobId}",
                AdditionalData = new { jobId, scheduledAt, lateSeconds }
            });
        }
    }
}
