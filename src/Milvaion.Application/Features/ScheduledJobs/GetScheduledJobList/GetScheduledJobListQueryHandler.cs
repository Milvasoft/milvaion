using Microsoft.EntityFrameworkCore;
using Milvaion.Application.Dtos.ScheduledJobDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Helpers;
using System.Linq.Expressions;

namespace Milvaion.Application.Features.ScheduledJobs.GetScheduledJobList;

/// <summary>
/// Handles the scheduledjob list operation.
/// </summary>
/// <param name="scheduledjobRepository"></param>
/// <param name="milvaionDbContextAccessor"></param>
public class GetScheduledJobListQueryHandler(IMilvaionRepositoryBase<ScheduledJob> scheduledjobRepository, IMilvaionDbContextAccessor milvaionDbContextAccessor) : IInterceptable, IListQueryHandler<GetScheduledJobListQuery, ScheduledJobListDto>
{
    private readonly IMilvaionRepositoryBase<ScheduledJob> _scheduledjobRepository = scheduledjobRepository;
    private readonly IMilvaionDbContextAccessor _milvaionDbContextAccessor = milvaionDbContextAccessor;

    /// <inheritdoc/>
    public async Task<ListResponse<ScheduledJobListDto>> Handle(GetScheduledJobListQuery request, CancellationToken cancellationToken)
    {
        Expression<Func<ScheduledJob, bool>> predicate = null;

        var searchTerm = $"%{request.SearchTerm?.Trim()}%";

        if (!string.IsNullOrWhiteSpace(searchTerm))
            predicate = c => EF.Functions.ILike(c.DisplayName, searchTerm) ||
                             EF.Functions.ILike(c.Tags, searchTerm) ||
                             EF.Functions.ILike(c.JobNameInWorker, searchTerm);

        var response = await _scheduledjobRepository.GetAllAsync(request, condition: predicate, projection: ScheduledJobListDto.Projection, cancellationToken: cancellationToken);

        if (!response.Data.IsNullOrEmpty())
        {
            var jobIds = response.Data.Select(x => x.Id).ToList();

            var relatedLatestOccurrences = await _milvaionDbContextAccessor.GetDbContext()
                                                                           .Set<JobOccurrence>()
                                                                           .AsNoTracking()
                                                                           .Where(o => jobIds.Contains(o.JobId))
                                                                           .Select(o => new
                                                                           {
                                                                               o.JobId,
                                                                               o.Status,
                                                                               LatestTime = o.EndTime ?? o.CreatedAt
                                                                           })
                                                                           .GroupBy(x => x.JobId)
                                                                           .Select(g => g.OrderByDescending(x => x.LatestTime).First()
                                                                           )
                                                                           .ToDictionaryAsync(x => x.JobId, cancellationToken);

            foreach (var job in response.Data)
            {
                if (relatedLatestOccurrences.TryGetValue(job.Id, out var occ))
                {
                    job.LatestStatus = occ.Status;
                    job.LatestRun = occ.LatestTime;
                }
            }
        }

        return response;
    }
}
