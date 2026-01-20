using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.InternalNotifications.UpdateInternalNotification;

/// <summary>
/// Handles the update of the internalNotification.
/// </summary>
/// <param name="InternalNotificationRepository"></param>
[Log]
public record UpdateInternalNotificationCommandHandler(IMilvaionRepositoryBase<InternalNotification> InternalNotificationRepository) : IInterceptable, ICommandHandler<UpdateInternalNotificationCommand, long>
{
    private readonly IMilvaionRepositoryBase<InternalNotification> _internalnotificationRepository = InternalNotificationRepository;

    /// <inheritdoc/>
    public async Task<Response<long>> Handle(UpdateInternalNotificationCommand request, CancellationToken cancellationToken)
    {
        var setPropertyBuilder = _internalnotificationRepository.GetUpdatablePropertiesBuilder(request);

        await _internalnotificationRepository.ExecuteUpdateAsync(request.Id, setPropertyBuilder, cancellationToken: cancellationToken);

        return Response<long>.Success(request.Id);
    }
}
