using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.InternalNotifications.DeleteInternalNotification;

/// <summary>
/// Handles the deletion of the internalNotification.
/// </summary>
/// <param name="InternalNotificationRepository"></param>
[Log]
public record DeleteInternalNotificationCommandHandler(IMilvaionRepositoryBase<InternalNotification> InternalNotificationRepository) : IInterceptable, ICommandHandler<DeleteInternalNotificationCommand, long>
{
    private readonly IMilvaionRepositoryBase<InternalNotification> _internalnotificationRepository = InternalNotificationRepository;

    /// <inheritdoc/>
    public async Task<Response<long>> Handle(DeleteInternalNotificationCommand request, CancellationToken cancellationToken)
    {
        var internalNotification = await _internalnotificationRepository.GetForDeleteAsync(request.InternalNotificationId, cancellationToken: cancellationToken);

        if (internalNotification == null)
            return Response<long>.Error(default, MessageKey.InternalNotificationNotFound);

        await _internalnotificationRepository.DeleteAsync(internalNotification, cancellationToken: cancellationToken);

        return Response<long>.Success(request.InternalNotificationId);
    }
}
