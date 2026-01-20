using Milvaion.Application.Dtos.ScheduledJobDtos;
using Milvaion.Application.Interfaces.Redis;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Enums;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.ScheduledJobs.GetScheduledJobDetail;

/// <summary>
/// Handles the scheduledjob detail operation.
/// </summary>
/// <param name="scheduledjobRepository"></param>
/// <param name="redisSchedulerService"></param>
public class GetScheduledJobDetailQueryHandler(IMilvaionRepositoryBase<ScheduledJob> scheduledjobRepository, IRedisSchedulerService redisSchedulerService) : IInterceptable, IQueryHandler<GetScheduledJobDetailQuery, ScheduledJobDetailDto>
{
    private readonly IMilvaionRepositoryBase<ScheduledJob> _scheduledjobRepository = scheduledjobRepository;
    private readonly IRedisSchedulerService _redisSchedulerService = redisSchedulerService;

    /// <inheritdoc/>
    public async Task<Response<ScheduledJobDetailDto>> Handle(GetScheduledJobDetailQuery request, CancellationToken cancellationToken)
    {
        var scheduledjob = await _scheduledjobRepository.GetByIdAsync(request.JobId, projection: ScheduledJobDetailDto.Projection, cancellationToken: cancellationToken);

        if (scheduledjob == null)
            return Response<ScheduledJobDetailDto>.Success(scheduledjob, MessageKey.JobNotFound, MessageType.Warning);

        scheduledjob.ExecuteAt = await _redisSchedulerService.GetScheduledTimeAsync(scheduledjob.Id, cancellationToken);

        return Response<ScheduledJobDetailDto>.Success(scheduledjob);
    }
}
