using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.FailedOccurrences.DeleteFailedOccurrence;

/// <summary>
/// Handles the deletion of the failedjob.
/// </summary>
/// <param name="FailedOccurrenceRepository"></param>
[Log]
[UserActivityTrack(UserActivity.DeleteFailedOccurrence)]
public record DeleteFailedOccurrenceCommandHandler(IMilvaionRepositoryBase<FailedOccurrence> FailedOccurrenceRepository) : IInterceptable, ICommandHandler<DeleteFailedOccurrenceCommand, List<Guid>>
{
    private readonly IMilvaionRepositoryBase<FailedOccurrence> _failedOccurrenceRepository = FailedOccurrenceRepository;

    /// <inheritdoc/>
    public async Task<Response<List<Guid>>> Handle(DeleteFailedOccurrenceCommand request, CancellationToken cancellationToken)
    {
        await _failedOccurrenceRepository.ExecuteDeleteAsync(f => request.FailedOccurrenceIdList.Contains(f.Id), cancellationToken: cancellationToken);

        return Response<List<Guid>>.Success(request.FailedOccurrenceIdList);
    }
}
