using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Milvaion.Application.Dtos.UpcomingExecutionDtos;
using Milvaion.Application.Interfaces.Redis;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.DataAccess.EfCore.Bulk;

namespace Milvaion.Application.Features.ScheduledJobs.GetUpcomingExecutionList;

/// <summary>
/// Builds the upcoming execution timeline.
/// </summary>
/// <remarks>
/// Two sources, because jobs and workflows are scheduled by two different services:
///
/// <list type="bullet">
///   <item>
///     Jobs come from the Redis sorted set the dispatcher polls. That set holds one
///     entry per job - the next run only - which is exactly what this screen needs.
///     The <c>ExecuteAt</c> column is deliberately not used: it is the configured
///     start time and stops matching reality as soon as a recurring job runs once.
///   </item>
///   <item>
///     Workflows are projected from their cron expressions, since the workflow engine
///     polls the database and never writes a next-run anywhere.
///   </item>
/// </list>
///
/// A third group has no time at all: jobs that are active and recurring but missing
/// from the sorted set. They will never fire, and nothing else in the product shows
/// this - the job list still calls them active and no occurrence is created to fail.
/// </remarks>
/// <param name="milvaionDbContextAccessor"></param>
/// <param name="redisScheduler"></param>
/// <param name="adminService"></param>
/// <param name="workflowEngineOptions"></param>
public class GetUpcomingExecutionListQueryHandler(IMilvaionDbContextAccessor milvaionDbContextAccessor,
                                                  IRedisSchedulerService redisScheduler,
                                                  IAdminService adminService,
                                                  IOptions<WorkflowEngineOptions> workflowEngineOptions) : IInterceptable, IQueryHandler<GetUpcomingExecutionListQuery, UpcomingExecutionListDto>
{
    private readonly IMilvaionDbContextAccessor _milvaionDbContextAccessor = milvaionDbContextAccessor;
    private readonly IRedisSchedulerService _redisScheduler = redisScheduler;
    private readonly IAdminService _adminService = adminService;
    private readonly WorkflowEngineOptions _workflowEngineOptions = workflowEngineOptions.Value;

    /// <summary>
    /// Upper bound on how many recurring jobs the unscheduled check will look at.
    /// </summary>
    /// <remarks>
    /// Two columns and one pipelined Redis call, so this is cheap - but it is a diagnostic
    /// band on a page that reloads often, and it should not grow without limit.
    /// </remarks>
    private const int _healthScanLimit = 2000;

    /// <inheritdoc/>
    public async Task<Response<UpcomingExecutionListDto>> Handle(GetUpcomingExecutionListQuery request, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var windowEnd = now.AddHours(request.WithinHours);
        var context = _milvaionDbContextAccessor.GetDbContext();

        var result = new UpcomingExecutionListDto
        {
            AsOfUtc = now,
            SchedulerReachable = IsSchedulerReachable(cancellationToken)
        };

        var items = new List<UpcomingExecutionDto>();

        if (request.Kind != UpcomingExecutionKind.Workflow)
        {
            items.AddRange(await GetScheduledJobsAsync(context, request, now, windowEnd, cancellationToken));

            var (unscheduled, truncated) = await GetUnscheduledJobsAsync(context, request, cancellationToken);

            items.AddRange(unscheduled);

            result.HealthScanTruncated = truncated;
        }

        if (request.Kind != UpcomingExecutionKind.Job)
            items.AddRange(await GetProjectedWorkflowsAsync(context, request, now, windowEnd, cancellationToken));

        result.NotScheduledCount = items.Count(i => i.Health == UpcomingExecutionHealth.NotScheduled);

        if (request.OnlyProblems)
            items = [.. items.Where(i => i.Health is UpcomingExecutionHealth.NotScheduled or UpcomingExecutionHealth.InvalidSchedule)];

        // Entries without a time sort last - they are not part of the timeline, they are the reason something is missing from it.
        items = [.. items.OrderBy(i => i.ScheduledAt ?? DateTime.MaxValue).ThenBy(i => i.DisplayName)];

        result.HasMore = items.Count > request.Limit;
        result.Items = [.. items.Take(request.Limit)];

        return Response<UpcomingExecutionListDto>.Success(result);
    }

    /// <summary>
    /// Jobs the dispatcher holds a run time for.
    /// </summary>
    private async Task<List<UpcomingExecutionDto>> GetScheduledJobsAsync(IMilvaBulkDbContextBase context,
                                                                        GetUpcomingExecutionListQuery request,
                                                                        DateTime now,
                                                                        DateTime windowEnd,
                                                                        CancellationToken cancellationToken)
    {
        List<KeyValuePair<Guid, DateTime>> scheduled;

        if (string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            // Sorted set read: one command, already ordered and bounded. This is the path
            // that has to stay cheap, since it runs on every page load.
            scheduled = await _redisScheduler.GetScheduledJobsInRangeAsync(now, windowEnd, request.Limit + 1, cancellationToken);
        }
        else
        {
            // Searching cannot start from Redis - the sorted set holds no names, so filtering
            // after the range read would only ever search the first page of the timeline.
            // Start from the database instead and ask Redis for the times of what matched.
            var searchTerm = $"%{request.SearchTerm.Trim()}%";

            var matchedIds = await context.Set<ScheduledJob>()
                                          .AsNoTracking()
                                          .Where(j => j.IsActive && EF.Functions.ILike(j.DisplayName, searchTerm))
                                          .Select(j => j.Id)
                                          .Take(_healthScanLimit)
                                          .ToListAsync(cancellationToken);

            var times = await _redisScheduler.GetScheduledTimesBulkAsync(matchedIds, cancellationToken);

            scheduled = [.. times.Where(t => t.Value.HasValue && t.Value.Value >= now && t.Value.Value <= windowEnd)
                                 .Select(t => new KeyValuePair<Guid, DateTime>(t.Key, t.Value.Value))];
        }

        if (scheduled.Count == 0)
            return [];

        var jobIds = scheduled.Select(s => s.Key).ToList();

        var jobs = await context.Set<ScheduledJob>()
                                .AsNoTracking()
                                .Where(j => jobIds.Contains(j.Id) && j.IsActive)
                                .Select(j => new JobRow
                                {
                                    Id = j.Id,
                                    DisplayName = j.DisplayName,
                                    CronExpression = j.CronExpression,
                                    WorkerId = j.WorkerId,
                                    JobNameInWorker = j.JobNameInWorker,
                                    Tags = j.Tags,
                                    IsExternal = j.IsExternal
                                })
                                .ToListAsync(cancellationToken);

        var jobsById = jobs.ToDictionary(j => j.Id);

        var items = new List<UpcomingExecutionDto>(scheduled.Count);

        foreach (var entry in scheduled)
        {
            // Present in Redis but not active in the database - a leftover the dispatcher's
            // startup recovery clears. Showing it would promise a run that will not happen.
            if (!jobsById.TryGetValue(entry.Key, out var job))
                continue;

            items.Add(ToDto(job, entry.Value, UpcomingExecutionHealth.Scheduled));
        }

        return items;
    }

    /// <summary>
    /// Recurring jobs that are active but absent from the sorted set, so nothing will run them.
    /// </summary>
    private async Task<(List<UpcomingExecutionDto> Items, bool Truncated)> GetUnscheduledJobsAsync(IMilvaBulkDbContextBase context,
                                                                                                  GetUpcomingExecutionListQuery request,
                                                                                                  CancellationToken cancellationToken)
    {
        var query = context.Set<ScheduledJob>()
                           .AsNoTracking()
                           // External jobs are absent from the sorted set by design - Milvaion
                           // never dispatches them, it only records what they already ran.
                           // One-time jobs leave it legitimately once they have fired, so only
                           // recurring jobs are expected to always be present.
                           .Where(j => j.IsActive && !j.IsExternal && j.CronExpression != null);

        if (!string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            var searchTerm = $"%{request.SearchTerm.Trim()}%";

            query = query.Where(j => EF.Functions.ILike(j.DisplayName, searchTerm));
        }

        var candidates = await query.OrderBy(j => j.DisplayName)
                                    .Select(j => new JobRow
                                    {
                                        Id = j.Id,
                                        DisplayName = j.DisplayName,
                                        CronExpression = j.CronExpression,
                                        WorkerId = j.WorkerId,
                                        JobNameInWorker = j.JobNameInWorker,
                                        Tags = j.Tags,
                                        IsExternal = j.IsExternal
                                    })
                                    .Take(_healthScanLimit + 1)
                                    .ToListAsync(cancellationToken);

        var truncated = candidates.Count > _healthScanLimit;

        if (truncated)
            candidates.RemoveAt(candidates.Count - 1);

        if (candidates.Count == 0)
            return ([], false);

        var times = await _redisScheduler.GetScheduledTimesBulkAsync(candidates.Select(c => c.Id), cancellationToken);

        var items = new List<UpcomingExecutionDto>();

        foreach (var candidate in candidates)
        {
            if (times.TryGetValue(candidate.Id, out var scheduledAt) && scheduledAt.HasValue)
                continue;

            items.Add(ToDto(candidate, scheduledAt: null, UpcomingExecutionHealth.NotScheduled));
        }

        return (items, truncated);
    }

    /// <summary>
    /// Workflows, projected from their cron expressions.
    /// </summary>
    private async Task<List<UpcomingExecutionDto>> GetProjectedWorkflowsAsync(IMilvaBulkDbContextBase context,
                                                                             GetUpcomingExecutionListQuery request,
                                                                             DateTime now,
                                                                             DateTime windowEnd,
                                                                             CancellationToken cancellationToken)
    {
        var query = context.Set<Workflow>()
                           .AsNoTracking()
                           .Where(w => w.IsActive && w.CronExpression != null);

        if (!string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            var searchTerm = $"%{request.SearchTerm.Trim()}%";

            query = query.Where(w => EF.Functions.ILike(w.Name, searchTerm));
        }

        var workflows = await query.Select(w => new WorkflowRow
        {
            Id = w.Id,
            DisplayName = w.Name,
            CronExpression = w.CronExpression,
            Tags = w.Tags,
            LastScheduledRunAt = w.LastScheduledRunAt
        })
                                   .Take(_healthScanLimit)
                                   .ToListAsync(cancellationToken);

        var items = new List<UpcomingExecutionDto>();

        foreach (var workflow in workflows)
        {
            var nextRun = CronProjector.GetNextOccurrence(workflow.CronExpression,
                                                          workflow.LastScheduledRunAt,
                                                          now,
                                                          _workflowEngineOptions.PollingIntervalSeconds);

            if (!nextRun.HasValue)
            {
                items.Add(new UpcomingExecutionDto
                {
                    Id = workflow.Id,
                    Kind = UpcomingExecutionKind.Workflow,
                    DisplayName = workflow.DisplayName,
                    ScheduledAt = null,
                    Health = UpcomingExecutionHealth.InvalidSchedule,
                    CronExpression = workflow.CronExpression,
                    IsRecurring = true,
                    Tags = workflow.Tags
                });

                continue;
            }

            // A projection past the window is simply not due yet. A projection in the past is
            // kept: it means the engine has not caught up and will trigger on its next poll.
            if (nextRun.Value > windowEnd)
                continue;

            items.Add(new UpcomingExecutionDto
            {
                Id = workflow.Id,
                Kind = UpcomingExecutionKind.Workflow,
                DisplayName = workflow.DisplayName,
                ScheduledAt = nextRun.Value,
                Health = UpcomingExecutionHealth.Projected,
                CronExpression = workflow.CronExpression,
                IsRecurring = true,
                Tags = workflow.Tags
            });
        }

        return items;
    }

    /// <summary>
    /// Reads the Redis circuit breaker so an outage is not reported as an empty schedule.
    /// </summary>
    /// <remarks>
    /// The Redis client fails open: when the circuit is tripped every read returns its
    /// fallback, which for the sorted set is an empty list. Without this the page would
    /// state that nothing is scheduled, which looks identical to a healthy but idle system.
    /// </remarks>
    private bool IsSchedulerReachable(CancellationToken cancellationToken)
    {
        try
        {
            var stats = _adminService.GetRedisCircuitBreakerStats(cancellationToken);

            return !string.Equals(stats?.Data?.State, "Open", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // The flag is context, not the answer. If it cannot be read, do not fail the page.
            return true;
        }
    }

    private static UpcomingExecutionDto ToDto(JobRow job, DateTime? scheduledAt, UpcomingExecutionHealth health) => new()
    {
        Id = job.Id,
        Kind = UpcomingExecutionKind.Job,
        DisplayName = job.DisplayName,
        ScheduledAt = scheduledAt,
        Health = health,
        CronExpression = job.CronExpression,
        IsRecurring = !string.IsNullOrWhiteSpace(job.CronExpression),
        WorkerId = job.WorkerId,
        JobNameInWorker = job.JobNameInWorker,
        Tags = job.Tags,
        IsExternal = job.IsExternal
    };

    private sealed class JobRow
    {
        public Guid Id { get; set; }
        public string DisplayName { get; set; }
        public string CronExpression { get; set; }
        public string WorkerId { get; set; }
        public string JobNameInWorker { get; set; }
        public string Tags { get; set; }
        public bool IsExternal { get; set; }
    }

    private sealed class WorkflowRow
    {
        public Guid Id { get; set; }
        public string DisplayName { get; set; }
        public string CronExpression { get; set; }
        public string Tags { get; set; }
        public DateTime? LastScheduledRunAt { get; set; }
    }
}
