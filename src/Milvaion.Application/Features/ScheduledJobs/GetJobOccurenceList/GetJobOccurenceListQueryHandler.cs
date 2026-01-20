using Microsoft.EntityFrameworkCore;
using Milvaion.Application.Dtos.ScheduledJobDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using System.Linq.Expressions;

namespace Milvaion.Application.Features.ScheduledJobs.GetJobOccurenceList;

/// <summary>
/// Handles the job occurrence list operation.
/// </summary>
/// <param name="scheduledjobRepository"></param>
public class GetJobOccurrenceListQueryHandler(IMilvaionRepositoryBase<JobOccurrence> scheduledjobRepository) : IInterceptable, IListQueryHandler<GetJobOccurrenceListQuery, JobOccurrenceListDto>
{
    private readonly IMilvaionRepositoryBase<JobOccurrence> _scheduledjobRepository = scheduledjobRepository;

    /// <inheritdoc/>
    public async Task<ListResponse<JobOccurrenceListDto>> Handle(GetJobOccurrenceListQuery request, CancellationToken cancellationToken)
    {
        Expression<Func<JobOccurrenceListDto, bool>> predicate = null;

        var searchTerm = $"%{request.SearchTerm?.Trim()}%";

        if (!string.IsNullOrWhiteSpace(searchTerm))
            predicate = c => EF.Functions.ILike(c.JobDisplayName, searchTerm) ||
                             EF.Functions.ILike(c.JobTags, searchTerm) ||
                             EF.Functions.ILike(c.JobName, searchTerm);

        var response = await _scheduledjobRepository.GetAllAsync(request,
                                                                 projection: JobOccurrenceListDto.Projection,
                                                                 conditionAfterProjection: predicate,
                                                                 splitQuery: true,
                                                                 cancellationToken: cancellationToken);

        return response;
    }
}
