using Microsoft.EntityFrameworkCore;
using Milvaion.Application.Dtos.ScheduledJobDtos;
using Milvaion.Application.Interfaces.Redis;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.DataAccess.EfCore.Utils;

namespace Milvaion.Application.Features.ScheduledJobs.GetJobOccurenceListCursor;

/// <summary>
/// Handles the job occurrence list operation.
/// </summary>
/// <param name="milvaionDbContextAccessor"></param>
/// <param name="redisStatsService"></param>
public class GetJobOccurenceListCursorQueryHandler(IMilvaionDbContextAccessor milvaionDbContextAccessor, IRedisStatsService redisStatsService, IProjectContext projectContext = null) : IInterceptable, IQueryHandler<GetJobOccurenceListCursorQuery, CursorListResponse<JobOccurrenceListDto>>
{
    private readonly IMilvaionDbContextAccessor _milvaionDbContextAccessor = milvaionDbContextAccessor;
    private readonly IRedisStatsService _redisStatsService = redisStatsService;

    /// <inheritdoc/>
    public async Task<Response<CursorListResponse<JobOccurrenceListDto>>> Handle(GetJobOccurenceListCursorQuery request, CancellationToken cancellationToken)
    {
        var manualCount = await JobOccurrenceListSearch.GetCountFromStatsAsync(request.Filtering, request.SearchTerm, _redisStatsService, cancellationToken, projectContext);

        var context = _milvaionDbContextAccessor.GetDbContext();

        var response = await context.Set<JobOccurrence>().Where(JobOccurrenceListSearch.BuildPredicate(request.SearchTerm)).ToCursorListResponseAsync(request, JobOccurrenceListDto.Projection, cancellationToken: cancellationToken);

        response.TotalDataCount = manualCount ?? response.TotalDataCount;

        return Response<CursorListResponse<JobOccurrenceListDto>>.Success(response);
    }
}