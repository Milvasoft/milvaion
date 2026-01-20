using Milvaion.Application.Dtos.FailedOccurrenceDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Enums;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.FailedOccurrences.GetFailedOccurrenceDetail;

/// <summary>
/// Handles the failedjob detail operation.
/// </summary>
/// <param name="failedOccurrenceRepository"></param>
public class GetFailedOccurrenceDetailQueryHandler(IMilvaionRepositoryBase<FailedOccurrence> failedOccurrenceRepository) : IInterceptable, IQueryHandler<GetFailedOccurrenceDetailQuery, FailedOccurrenceDetailDto>
{
    private readonly IMilvaionRepositoryBase<FailedOccurrence> _failedOccurrenceRepository = failedOccurrenceRepository;

    /// <inheritdoc/>
    public async Task<Response<FailedOccurrenceDetailDto>> Handle(GetFailedOccurrenceDetailQuery request, CancellationToken cancellationToken)
    {
        var failedjob = await _failedOccurrenceRepository.GetByIdAsync(request.FailedOccurrenceId, projection: FailedOccurrenceDetailDto.Projection, cancellationToken: cancellationToken);

        if (failedjob == null)
            return Response<FailedOccurrenceDetailDto>.Success(failedjob, MessageKey.FailedOccurrenceNotFound, MessageType.Warning);

        return Response<FailedOccurrenceDetailDto>.Success(failedjob);
    }
}
