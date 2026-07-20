using Milvaion.Application.Interfaces.Redis;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.ScheduledJobs.DeleteScheduledJob;

/// <summary>
/// Handles the deletion of the scheduledjob.
/// </summary>
[Log]
[UserActivityTrack(UserActivity.DeleteScheduledJob)]
public record DeleteScheduledJobCommandHandler(IMilvaionRepositoryBase<ScheduledJob> ScheduledJobRepository,
                                               IRedisSchedulerService RedisSchedulerService,
                                               IRedisCancellationService RedisCancellationService,
                                               IJobOccurrenceEventPublisher EventPublisher) : IInterceptable, ICommandHandler<DeleteScheduledJobCommand, Guid>
{
    /// <inheritdoc/>
    public async Task<Response<Guid>> Handle(DeleteScheduledJobCommand request, CancellationToken cancellationToken)
    {

        var isRunning = await RedisSchedulerService.IsJobRunningAsync(request.JobId, cancellationToken);

        if (isRunning)
            return Response<Guid>.Error(default, "Cannot delete a running job");


        var scheduledjob = await ScheduledJobRepository.GetByIdAsync(request.JobId, cancellationToken: cancellationToken);

        if (scheduledjob == null)
            return Response<Guid>.Error(default, MessageKey.JobNotFound);

        // 1. Remove from Redis ZSET
        await RedisSchedulerService.RemoveFromScheduledSetAsync(request.JobId, cancellationToken);

        // 2. Remove from Redis cache
        await RedisSchedulerService.RemoveCachedJobAsync(request.JobId, cancellationToken);

        // 3. Delete from database
        await ScheduledJobRepository.DeleteAsync(scheduledjob, cancellationToken: cancellationToken);

        return Response<Guid>.Success(request.JobId);
    }
}
