using Milvaion.Application.Dtos.DashboardDtos;
using Milvaion.Application.Interfaces.Redis;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.Dashboard.GetDashboard;

/// <summary>
/// Handles the dashboard statistics query using Redis counters (real-time, no SQL!).
/// </summary>
public class GetDashboardQueryHandler(IRedisStatsService redisStatsService, IRedisWorkerService redisWorkerService) : IInterceptable, IQueryHandler<GetDashboardQuery, DashboardDto>
{
    private readonly IRedisStatsService _redisStatsService = redisStatsService;
    private readonly IRedisWorkerService _redisWorkerService = redisWorkerService;

    /// <inheritdoc/>
    public async Task<Response<DashboardDto>> Handle(GetDashboardQuery request, CancellationToken cancellationToken)
    {
        // Get all statistics from Redis in parallel (real-time, no SQL query!)
        var statsTask = _redisStatsService.GetStatisticsAsync(cancellationToken);
        var workersTask = _redisWorkerService.GetAllWorkersAsync(cancellationToken);
        var epmTask = _redisStatsService.GetExecutionsPerMinuteAsync(cancellationToken);

        await Task.WhenAll(statsTask, workersTask, epmTask);

        var stats = await statsTask;
        var workers = await workersTask;
        var epm = await epmTask;

        // Calculate derived metrics
        var totalExecutions = stats.GetValueOrDefault("Total", 0).ToSaturatedInt();
        var queuedJobs = stats.GetValueOrDefault("Queued", 0).ToSaturatedInt();
        var runningJobs = stats.GetValueOrDefault("Running", 0).ToSaturatedInt();
        var completedJobs = stats.GetValueOrDefault("Completed", 0).ToSaturatedInt();
        var failedJobs = stats.GetValueOrDefault("Failed", 0).ToSaturatedInt();
        var cancelledJobs = stats.GetValueOrDefault("Cancelled", 0).ToSaturatedInt();
        var timedOutJobs = stats.GetValueOrDefault("TimedOut", 0).ToSaturatedInt();

        // Calculate average duration from sum and count
        var durationSum = stats.GetValueOrDefault("DurationSum", 0);
        var durationCount = stats.GetValueOrDefault("DurationCount", 0);
        double? avgDuration = durationCount > 0 ? (double)durationSum / durationCount : null;

        double successRate = 0;
        var finishedJobs = completedJobs + failedJobs + cancelledJobs + timedOutJobs;

        if (finishedJobs > 0)
            successRate = completedJobs * 100.0 / finishedJobs;

        // Get worker statistics from Redis
        var activeWorkers = workers.Where(w => w.Status == WorkerStatus.Active).ToList();

        var statistics = new DashboardDto
        {
            TotalExecutions = totalExecutions,
            QueuedJobs = queuedJobs,
            RunningJobs = runningJobs,
            CompletedJobs = completedJobs,
            FailedJobs = failedJobs,
            CancelledJobs = cancelledJobs,
            TimedOutJobs = timedOutJobs,
            SuccessRate = successRate,
            TotalWorkers = activeWorkers.Count,
            TotalWorkerInstances = activeWorkers.Sum(w => w.Instances?.Count ?? 0),
            WorkerCurrentJobs = activeWorkers.Sum(w => w.CurrentJobs),
            WorkerMaxCapacity = activeWorkers.Sum(w => w.MaxParallelJobs),
            WorkerUtilization = activeWorkers.Sum(w => w.MaxParallelJobs) > 0 ? (double)activeWorkers.Sum(w => w.CurrentJobs) / activeWorkers.Sum(w => w.MaxParallelJobs) * 100 : 0,
            ExecutionsPerSecond = epm / 60.0,
            ExecutionsPerMinute = epm,
            AverageDuration = avgDuration,
            RecentExecutions = (int?)epm, // Last 60 seconds
        };

        return Response<DashboardDto>.Success(statistics);
    }
}
