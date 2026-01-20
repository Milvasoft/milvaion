using Microsoft.AspNetCore.Http;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Ef.Transaction;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.FailedOccurrences.UpdateFailedOccurrence;

/// <summary>
/// Handles the update of the failedjob.
/// </summary>
/// <param name="FailedOccurrenceRepository"></param>
/// <param name="HttpContextAccessor"></param>
[Log]
[Transaction]
[UserActivityTrack(UserActivity.UpdateFailedOccurrence)]
public record UpdateFailedOccurrenceCommandHandler(IMilvaionRepositoryBase<FailedOccurrence> FailedOccurrenceRepository,
                                            IHttpContextAccessor HttpContextAccessor) : IInterceptable, ICommandHandler<UpdateFailedOccurrenceCommand, List<Guid>>
{
    private readonly IMilvaionRepositoryBase<FailedOccurrence> _failedOccurrenceRepository = FailedOccurrenceRepository;
    private readonly IHttpContextAccessor _httpContextAccessor = HttpContextAccessor;

    /// <inheritdoc/>
    public async Task<Response<List<Guid>>> Handle(UpdateFailedOccurrenceCommand request, CancellationToken cancellationToken)
    {
        var setPropertyBuilder = _failedOccurrenceRepository.GetUpdatablePropertiesBuilder(request);

        if (request.Resolved.IsUpdated && request.Resolved.Value)
        {
            if (!request.ResolvedAt.IsUpdated)
                setPropertyBuilder = setPropertyBuilder.SetPropertyValue(x => x.ResolvedAt, DateTime.UtcNow);

            if (!request.ResolvedBy.IsUpdated)
                setPropertyBuilder = setPropertyBuilder.SetPropertyValue(x => x.ResolvedBy, _httpContextAccessor.HttpContext.CurrentUserName() ?? "Anonymus");
        }

        await _failedOccurrenceRepository.ExecuteUpdateAsync(f => request.IdList.Contains(f.Id), setPropertyBuilder, cancellationToken: cancellationToken);

        return Response<List<Guid>>.Success(request.IdList);
    }
}
