using Microsoft.EntityFrameworkCore;
using Milvaion.Application.Dtos.ScheduledJobDtos;
using Milvaion.Application.Interfaces.Redis;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.DataAccess.EfCore.Utils;

namespace Milvaion.Application.Features.ScheduledJobs.GetJobOccurenceList;

/// <summary>
/// Handles the job occurrence list operation.
/// </summary>
/// <param name="milvaionDbContextAccessor"></param>
/// <param name="redisStatsService"></param>
public class GetJobOccurrenceListQueryHandler(IMilvaionDbContextAccessor milvaionDbContextAccessor, IRedisStatsService redisStatsService) : IInterceptable, IListQueryHandler<GetJobOccurrenceListQuery, JobOccurrenceListDto>
{
    private readonly IMilvaionDbContextAccessor _milvaionDbContextAccessor = milvaionDbContextAccessor;
    private readonly IRedisStatsService _redisStatsService = redisStatsService;

    /// <inheritdoc/>
    public async Task<ListResponse<JobOccurrenceListDto>> Handle(GetJobOccurrenceListQuery request, CancellationToken cancellationToken)
    {
        var manualCount = await JobOccurrenceListSearch.GetCountFromStatsAsync(request.Filtering, request.SearchTerm, _redisStatsService, cancellationToken);

        var context = _milvaionDbContextAccessor.GetDbContext();

        var response = await context.Set<JobOccurrence>().Where(JobOccurrenceListSearch.BuildPredicate(request.SearchTerm)).ToListResponseAsync(request, JobOccurrenceListDto.Projection, manualCount, cancellationToken: cancellationToken);

        return response;
    }
}