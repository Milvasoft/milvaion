using Cronos;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Milvaion.Application.Interfaces.Redis;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.ScheduledJobs.CreateScheduledJob;

/// <summary>
/// Handles the creation of the scheduledjob.
/// </summary>
/// <param name="ScheduledJobRepository"></param>
/// <param name="RedisSchedulerService"></param>
/// <param name="RedisWorkerService"></param>
[Log]
[UserActivityTrack(UserActivity.CreateScheduledJob)]
public record CreateScheduledJobCommandHandler(IMilvaionRepositoryBase<ScheduledJob> ScheduledJobRepository,
                                               IRedisSchedulerService RedisSchedulerService,
                                               IRedisWorkerService RedisWorkerService) : IInterceptable, ICommandHandler<CreateScheduledJobCommand, Guid>
{
    private readonly IMilvaionRepositoryBase<ScheduledJob> _scheduledjobRepository = ScheduledJobRepository;
    private readonly IRedisSchedulerService _redisSchedulerService = RedisSchedulerService;
    private readonly IRedisWorkerService _redisWorkerService = RedisWorkerService;

    /// <inheritdoc/>
    public async Task<Response<Guid>> Handle(CreateScheduledJobCommand request, CancellationToken cancellationToken)
    {
        // Calculate ExecuteAt from CronExpression if provided
        DateTime executeAt;

        if (!string.IsNullOrWhiteSpace(request.CronExpression))
        {
            var cronExpression = CronExpression.Parse(request.CronExpression, CronFormat.IncludeSeconds);

            executeAt = cronExpression.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Utc)!.Value;
        }
        else
        {
            // FIX: If ExecuteAt from frontend is in the past or very near (< 5 seconds), set to now
            var requestedTime = request.ExecuteAt;
            var now = DateTime.UtcNow;

            if (requestedTime <= now.AddSeconds(5))
                executeAt = now;
            else
                executeAt = requestedTime;
        }

        // Create job with calculated/validated ExecuteAt
        var scheduledjob = request.Adapt<ScheduledJob>();

        // Map SelectedJobName to JobNameInWorker (different property names)
        scheduledjob.JobNameInWorker = request.SelectedJobName;
        scheduledjob.ExecuteAt = executeAt;
        scheduledjob.FixJobData();

        // If WorkerId is specified, validate worker from Redis
        if (!string.IsNullOrWhiteSpace(request.WorkerId))
        {
            var cachedWorker = await _redisWorkerService.GetWorkerAsync(request.WorkerId, cancellationToken);

            if (cachedWorker == null)
                return Response<Guid>.Error(default, $"Worker {request.WorkerId} not found");

            if (cachedWorker.Status != WorkerStatus.Active)
                return Response<Guid>.Error(default, $"Worker {request.WorkerId} is not active (Status: {cachedWorker.Status})");

            // Validate that job type is supported by the worker
            var jobNameToValidate = !string.IsNullOrWhiteSpace(request.SelectedJobName) ? request.SelectedJobName : scheduledjob.JobNameInWorker;

            if (!cachedWorker.JobNames.Contains(jobNameToValidate))
                return Response<Guid>.Error(default, $"Worker {request.WorkerId} does not support job type '{jobNameToValidate}'. "
                                                     + $"Supported types: {string.Join(", ", cachedWorker.JobNames)}");

            // Use selected job name if specified
            if (!string.IsNullOrWhiteSpace(request.SelectedJobName))
                scheduledjob.JobNameInWorker = request.SelectedJobName;

            // Copy worker's routing pattern to job
            scheduledjob.RoutingPattern = cachedWorker.RoutingPatterns[request.SelectedJobName];
        }

        // Save to database
        await _scheduledjobRepository.AddAsync(scheduledjob, cancellationToken);

        // Add to Redis ZSET for time-based scheduling
        await _redisSchedulerService.AddToScheduledSetAsync(scheduledjob.Id,
                                                            scheduledjob.ExecuteAt,
                                                            cancellationToken);

        // Cache job details in Redis Hash
        await _redisSchedulerService.CacheJobDetailsAsync(scheduledjob,
                                                          ttl: TimeSpan.FromHours(24), // 24 hour cache
                                                          cancellationToken);

        return Response<Guid>.Success(scheduledjob.Id);
    }
}
