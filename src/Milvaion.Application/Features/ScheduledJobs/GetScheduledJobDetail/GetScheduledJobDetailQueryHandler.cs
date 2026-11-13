using Milvaion.Application.Dtos.ExclusionCalendarDtos;
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
/// <param name="exclusionCalendarRepository"></param>
/// <param name="redisSchedulerService"></param>
public class GetScheduledJobDetailQueryHandler(IMilvaionRepositoryBase<ScheduledJob> scheduledjobRepository,
                                               IMilvaionRepositoryBase<ExclusionCalendar> exclusionCalendarRepository,
                                               IRedisSchedulerService redisSchedulerService) : IInterceptable, IQueryHandler<GetScheduledJobDetailQuery, ScheduledJobDetailDto>
{
    private readonly IMilvaionRepositoryBase<ScheduledJob> _scheduledjobRepository = scheduledjobRepository;
    private readonly IMilvaionRepositoryBase<ExclusionCalendar> _exclusionCalendarRepository = exclusionCalendarRepository;
    private readonly IRedisSchedulerService _redisSchedulerService = redisSchedulerService;

    /// <inheritdoc/>
    public async Task<Response<ScheduledJobDetailDto>> Handle(GetScheduledJobDetailQuery request, CancellationToken cancellationToken)
    {
        var scheduledjob = await _scheduledjobRepository.GetByIdAsync(request.JobId, projection: ScheduledJobDetailDto.Projection, cancellationToken: cancellationToken);

        if (scheduledjob == null)
            return Response<ScheduledJobDetailDto>.Success(scheduledjob, MessageKey.JobNotFound, MessageType.Warning);

        scheduledjob.ExecuteAt = await _redisSchedulerService.GetScheduledTimeAsync(scheduledjob.Id, cancellationToken);

        scheduledjob.ExclusionCalendars = [];

        if (scheduledjob.ExclusionCalendarIds?.Count > 0)
        {
            var ids = scheduledjob.ExclusionCalendarIds;

            var calendars = await _exclusionCalendarRepository.GetAllAsync(c => ids.Contains(c.Id),
                                                                           projection: c => new ExclusionCalendar { Id = c.Id, Name = c.Name },
                                                                           cancellationToken: cancellationToken);

            var names = calendars.ToDictionary(c => c.Id, c => c.Name);

            scheduledjob.ExclusionCalendars = [.. ids.Where(names.ContainsKey).Select(id => new ExclusionCalendarRefDto { Id = id, Name = names[id] })];
        }

        return Response<ScheduledJobDetailDto>.Success(scheduledjob);
    }
}
