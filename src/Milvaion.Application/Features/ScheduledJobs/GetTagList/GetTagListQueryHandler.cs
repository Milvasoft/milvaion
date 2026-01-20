using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.ScheduledJobs.GetTagList;

/// <summary>
/// Handles the scheduledjob list operation.
/// </summary>
/// <param name="scheduledjobRepository"></param>
public class GetTagListQueryHandler(IMilvaionRepositoryBase<ScheduledJob> scheduledjobRepository) : IInterceptable, IQueryHandler<GetTagListQuery, List<string>>
{
    private readonly IMilvaionRepositoryBase<ScheduledJob> _scheduledjobRepository = scheduledjobRepository;

    /// <inheritdoc/>
    public async Task<Response<List<string>>> Handle(GetTagListQuery request, CancellationToken cancellationToken)
    {
        var response = await _scheduledjobRepository.GetAllAsync(projection: ScheduledJob.Projections.TagList, cancellationToken: cancellationToken);

        List<string> allTags = response.SelectMany(s => s.Tags.Split(','))?.Distinct()?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? [];

        return Response<List<string>>.Success(allTags);
    }
}
