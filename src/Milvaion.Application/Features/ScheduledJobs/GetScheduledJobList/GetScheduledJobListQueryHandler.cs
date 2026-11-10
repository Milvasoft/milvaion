using Microsoft.EntityFrameworkCore;
using Milvaion.Application.Dtos.ScheduledJobDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
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

        if (!string.IsNullOrWhiteSpace(request.SearchTerm))
        {

            if (Guid.TryParse(request.SearchTerm, out var guid))
            {
                predicate = c => c.Id == guid;
            }
            else
            {
                string searchTerm = request.SearchTerm.ToContainsPattern();

                predicate = c => EF.Functions.ILike(c.DisplayName, searchTerm, MilvaionExtensions.LikeEscapeCharacter) ||
                                 EF.Functions.ILike(c.Tags, searchTerm, MilvaionExtensions.LikeEscapeCharacter) ||
                                 EF.Functions.ILike(c.JobNameInWorker, searchTerm, MilvaionExtensions.LikeEscapeCharacter);
            }
        }

        var response = await _scheduledjobRepository.GetAllAsync(request, condition: predicate, projection: ScheduledJobListDto.Projection, cancellationToken: cancellationToken);

        return response;
    }
}
