using Microsoft.EntityFrameworkCore;
using Milvaion.Application.Dtos.ExclusionCalendarDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Components.Rest.Request;
using Milvasoft.Core.Abstractions;
using System.Linq.Expressions;

namespace Milvaion.Application.Features.ExclusionCalendars.GetExclusionCalendarList;

/// <summary>
/// Lists exclusion calendars.
/// </summary>
public record GetExclusionCalendarListQuery : ListRequest, IListRequestQuery<ExclusionCalendarListDto>
{
    /// <summary>
    /// Free text search over calendar names and descriptions.
    /// </summary>
    public string SearchTerm { get; set; }
}

/// <summary>
/// Handles the exclusion calendar list.
/// </summary>
/// <param name="calendarRepository"></param>
public class GetExclusionCalendarListQueryHandler(IMilvaionRepositoryBase<ExclusionCalendar> calendarRepository) : IInterceptable, IListQueryHandler<GetExclusionCalendarListQuery, ExclusionCalendarListDto>
{
    private readonly IMilvaionRepositoryBase<ExclusionCalendar> _calendarRepository = calendarRepository;

    /// <inheritdoc/>
    public Task<ListResponse<ExclusionCalendarListDto>> Handle(GetExclusionCalendarListQuery request, CancellationToken cancellationToken)
    {
        Expression<Func<ExclusionCalendar, bool>> predicate = null;

        if (!string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            var searchTerm = request.SearchTerm.ToContainsPattern();

            predicate = c => EF.Functions.ILike(c.Name, searchTerm, MilvaionExtensions.LikeEscapeCharacter)
                             || EF.Functions.ILike(c.Description, searchTerm, MilvaionExtensions.LikeEscapeCharacter);
        }

        return _calendarRepository.GetAllAsync(request, condition: predicate, projection: ExclusionCalendarListDto.Projection, cancellationToken: cancellationToken);
    }
}
