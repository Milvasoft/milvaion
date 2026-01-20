using Microsoft.EntityFrameworkCore;
using Milvaion.Application.Dtos.DashboardDtos;
using Milvaion.Application.Interfaces.Redis;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.Dashboard.GetDashboard;

/// <summary>
/// Handles the dashboard statistics query.
/// </summary>
/// <param name="milvaionDbContextAccessor"></param>
/// <param name="redisWorkerService"></param>
public class GetDashboardQueryHandler(IMilvaionDbContextAccessor milvaionDbContextAccessor,
                                      IRedisWorkerService redisWorkerService) : IInterceptable, IQueryHandler<GetDashboardQuery, DashboardDto>
{
    private readonly IMilvaionDbContextAccessor _milvaionDbContextAccessor = milvaionDbContextAccessor;
    private readonly IRedisWorkerService _redisWorkerService = redisWorkerService;

    /// <inheritdoc/>
    public async Task<Response<DashboardDto>> Handle(GetDashboardQuery request, CancellationToken cancellationToken)
    {
        var context = _milvaionDbContextAccessor.GetDbContext();
        var now = DateTime.UtcNow;

        // Filter for last 7 days
        var sevenDaysAgo = now.AddDays(-7);

        // Single query to get all statistics using GroupBy
        var statistics = await context.Set<JobOccurrence>()
                                      .AsNoTracking()
                                      .Where(o => o.CreatedAt >= sevenDaysAgo) // Last 7 days filter
                                      .GroupBy(o => 1) // Group all records together
                                      .Select(g => new DashboardDto
                                      {
                                          TotalExecutions = g.Count(),
                                          QueuedJobs = g.Count(o => o.Status == JobOccurrenceStatus.Queued),
                                          CompletedJobs = g.Count(o => o.Status == JobOccurrenceStatus.Completed),
                                          FailedOccurrences = g.Count(o => o.Status == JobOccurrenceStatus.Failed),
                                          CancelledJobs = g.Count(o => o.Status == JobOccurrenceStatus.Cancelled),
                                          TimedOutJobs = g.Count(o => o.Status == JobOccurrenceStatus.TimedOut),
                                          RunningJobs = g.Count(o => o.Status == JobOccurrenceStatus.Running),
                                          AverageDuration = g.Where(o => o.DurationMs.HasValue && o.Status == JobOccurrenceStatus.Completed)
                                                             .Average(o => (double?)o.DurationMs),
                                          SuccessRate = g.Count() > 0
                                              ? (double)g.Count(o => o.Status == JobOccurrenceStatus.Completed) / g.Count() * 100
                                              : 0
                                      }).FirstOrDefaultAsync(cancellationToken) ?? new();

        // Calculate throughput metrics (executions per minute/second)
        // Use last 7 days data to calculate average throughput
        if (statistics.TotalExecutions > 0)
        {
            // Calculate REAL-TIME throughput (last 30 seconds)
            var thirtySecondsAgo = now.AddSeconds(-30);
            var recentExecutions = await context.Set<JobOccurrence>()
                                                .AsNoTracking()
                                                .CountAsync(o => o.CreatedAt >= thirtySecondsAgo, cancellationToken);

            statistics.ExecutionsPerSecond = recentExecutions / 30.0; // Jobs per second (30s window)
            statistics.ExecutionsPerMinute = statistics.ExecutionsPerSecond * 60.0; // Extrapolate to per minute
        }

        // Calculate peak executions per minute (last hour, grouped by minute)
        var oneHourAgo = now.AddHours(-1);
        var peakPerMinute = await context.Set<JobOccurrence>()
                                         .AsNoTracking()
                                         .Where(o => o.CreatedAt >= oneHourAgo)
                                         .GroupBy(o => new
                                         {
                                             o.CreatedAt.Year,
                                             o.CreatedAt.Month,
                                             o.CreatedAt.Day,
                                             o.CreatedAt.Hour,
                                             o.CreatedAt.Minute
                                         })
                                         .Select(g => g.Count())
                                         .OrderByDescending(count => count)
                                         .FirstOrDefaultAsync(cancellationToken);

        statistics.PeakExecutionsPerMinute = peakPerMinute > 0 ? peakPerMinute : null;

        // Get worker statistics from Redis
        var workers = await _redisWorkerService.GetAllWorkersAsync(cancellationToken);

        var activeWorkers = workers.Where(w => w.Status == WorkerStatus.Active).ToList();

        statistics.TotalWorkers = activeWorkers.Count;
        statistics.TotalWorkerInstances = activeWorkers.Sum(w => w.Instances?.Count ?? 0);
        statistics.WorkerCurrentJobs = activeWorkers.Sum(w => w.CurrentJobs);
        statistics.WorkerMaxCapacity = activeWorkers.Sum(w => w.MaxParallelJobs);
        statistics.WorkerUtilization = statistics.WorkerMaxCapacity > 0 ? (double)statistics.WorkerCurrentJobs / statistics.WorkerMaxCapacity * 100 : 0;

        return Response<DashboardDto>.Success(statistics);
    }
}
