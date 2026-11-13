using Mapster;
using Milvaion.Application.Interfaces.Redis;
using Milvaion.Application.Utils.Scheduling;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.Enums;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.ScheduledJobs.CreateScheduledJob;

/// <summary>
/// Handles the creation of the scheduledjob.
/// </summary>
/// <param name="ScheduledJobRepository"></param>
/// <param name="ExclusionCalendarRepository"></param>
/// <param name="RedisSchedulerService"></param>
/// <param name="RedisWorkerService"></param>
[Log]
[UserActivityTrack(UserActivity.CreateScheduledJob)]
public record CreateScheduledJobCommandHandler(IMilvaionRepositoryBase<ScheduledJob> ScheduledJobRepository,
                                               IMilvaionRepositoryBase<ExclusionCalendar> ExclusionCalendarRepository,
                                               IRedisSchedulerService RedisSchedulerService,
                                               IRedisWorkerService RedisWorkerService) : IInterceptable, ICommandHandler<CreateScheduledJobCommand, Guid>
{
    private readonly IMilvaionRepositoryBase<ScheduledJob> _scheduledjobRepository = ScheduledJobRepository;
    private readonly IMilvaionRepositoryBase<ExclusionCalendar> _exclusionCalendarRepository = ExclusionCalendarRepository;
    private readonly IRedisSchedulerService _redisSchedulerService = RedisSchedulerService;
    private readonly IRedisWorkerService _redisWorkerService = RedisWorkerService;

    /// <inheritdoc/>
    public async Task<Response<Guid>> Handle(CreateScheduledJobCommand request, CancellationToken cancellationToken)
    {
        request.TimeZoneId = string.IsNullOrWhiteSpace(request.TimeZoneId) ? ScheduledJob.DefaultTimeZoneId : request.TimeZoneId;
        request.ExclusionCalendarIds = ExclusionCalendarReferences.Normalize(request.ExclusionCalendarIds);

        var missingCalendars = await ExclusionCalendarReferences.FindMissingAsync(_exclusionCalendarRepository, request.ExclusionCalendarIds, cancellationToken);

        if (missingCalendars is not null)
            return Response<Guid>.Error(default, missingCalendars);

        // Calculate ExecuteAt from CronExpression if provided
        DateTime executeAt;

        if (!string.IsNullOrWhiteSpace(request.CronExpression))
        {
            var cronExpression = ZonedCron.Parse(request.CronExpression);

            // Evaluated in the job's own zone, the same way the dispatcher will evaluate it.
            var nextOccurrence = ZonedCron.GetNextOccurrence(cronExpression, DateTime.UtcNow, ZonedCron.ResolveTimeZone(request.TimeZoneId));

            if (!nextOccurrence.HasValue)
                return Response<Guid>.Error(default, MessageKey.InvalidCronExpression);

            executeAt = nextOccurrence.Value;
        }
        else
        {
            // If ExecuteAt from frontend is in the past or very near (< 5 seconds), set to now
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

            if (cachedWorker.Metadata.IsExternal && !request.InternalRequest)
                return Response<Guid>.Error(default, $"Worker {request.WorkerId} is an external worker and cannot be assigned jobs directly.");

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
        var scheduled = await _redisSchedulerService.AddToScheduledSetAsync(scheduledjob.Id,
                                                                            scheduledjob.ExecuteAt,
                                                                            cancellationToken);

        // Cache job details in Redis Hash
        await _redisSchedulerService.CacheJobDetailsAsync(scheduledjob,
                                                          ttl: TimeSpan.FromHours(24), // 24 hour cache
                                                          cancellationToken);

        var response = Response<Guid>.Success(scheduledjob.Id);

        // The job is safely stored, so this is not a failure, but until the next startup recovery it will not run.
        if (!scheduled && scheduledjob.IsActive)
            response.AddMessage("The job was saved but could not be added to the schedule. It will start after the next API restart.", MessageType.Warning);

        return response;
    }
}
