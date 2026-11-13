using Milvaion.Application.Dtos.ExclusionCalendarDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Enums;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.ExclusionCalendars.GetExclusionCalendarDetail;

/// <summary>
/// Gets one exclusion calendar.
/// </summary>
public record GetExclusionCalendarDetailQuery : IQuery<ExclusionCalendarDetailDto>
{
    /// <summary>Id of the calendar.</summary>
    public Guid CalendarId { get; set; }
}

/// <summary>
/// Handles the exclusion calendar detail.
/// </summary>
/// <param name="calendarRepository"></param>
/// <param name="jobRepository"></param>
public class GetExclusionCalendarDetailQueryHandler(IMilvaionRepositoryBase<ExclusionCalendar> calendarRepository,
                                                    IMilvaionRepositoryBase<ScheduledJob> jobRepository) : IInterceptable, IQueryHandler<GetExclusionCalendarDetailQuery, ExclusionCalendarDetailDto>
{
    private readonly IMilvaionRepositoryBase<ExclusionCalendar> _calendarRepository = calendarRepository;
    private readonly IMilvaionRepositoryBase<ScheduledJob> _jobRepository = jobRepository;

    /// <inheritdoc/>
    public async Task<Response<ExclusionCalendarDetailDto>> Handle(GetExclusionCalendarDetailQuery request, CancellationToken cancellationToken)
    {
        var calendar = await _calendarRepository.GetByIdAsync(request.CalendarId, projection: ExclusionCalendarDetailDto.Projection, cancellationToken: cancellationToken);

        if (calendar is null)
            return Response<ExclusionCalendarDetailDto>.Success(calendar, "Exclusion calendar not found.", MessageType.Warning);

        var calendarId = request.CalendarId;

        var jobs = await _jobRepository.GetAllAsync(j => j.ExclusionCalendarIds.Contains(calendarId),
                                                    projection: j => new ScheduledJob { Id = j.Id, DisplayName = j.DisplayName, IsActive = j.IsActive },
                                                    cancellationToken: cancellationToken);

        calendar.Jobs = [.. jobs.OrderBy(j => j.DisplayName).Select(j => new ExclusionCalendarJobRefDto { Id = j.Id, DisplayName = j.DisplayName, IsActive = j.IsActive })];

        return Response<ExclusionCalendarDetailDto>.Success(calendar);
    }
}
