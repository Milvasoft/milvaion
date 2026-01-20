using Microsoft.EntityFrameworkCore;
using Milvaion.Application.Dtos.FailedOccurrenceDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using System.Linq.Expressions;

namespace Milvaion.Application.Features.FailedOccurrences.GetFailedOccurrenceList;

/// <summary>
/// Handles the failedjob list operation.
/// </summary>
/// <param name="failedOccurrenceRepository"></param>
public class GetFailedOccurrenceListQueryHandler(IMilvaionRepositoryBase<FailedOccurrence> failedOccurrenceRepository) : IInterceptable, IListQueryHandler<GetFailedOccurrenceListQuery, FailedOccurrenceListDto>
{
    private readonly IMilvaionRepositoryBase<FailedOccurrence> _failedOccurrenceRepository = failedOccurrenceRepository;

    /// <inheritdoc/>
    public async Task<ListResponse<FailedOccurrenceListDto>> Handle(GetFailedOccurrenceListQuery request, CancellationToken cancellationToken)
    {
        Expression<Func<FailedOccurrence, bool>> predicate = null;

        var searchTerm = $"%{request.SearchTerm?.Trim()}%";

        if (!string.IsNullOrWhiteSpace(searchTerm))
            predicate = c => EF.Functions.ILike(c.JobDisplayName, searchTerm) ||
                             EF.Functions.ILike(c.JobNameInWorker, searchTerm) ||
                             EF.Functions.ILike(c.ResolutionNote, searchTerm);

        var response = await _failedOccurrenceRepository.GetAllAsync(request, condition: predicate, projection: FailedOccurrenceListDto.Projection, cancellationToken: cancellationToken);

        return response;
    }
}
