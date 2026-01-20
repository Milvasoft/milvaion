using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.InternalNotifications.CreateInternalNotification;

/// <summary>
/// Handles the creation of the internalNotification.
/// </summary>
/// <param name="NotificationService"></param>
[Log]
public record CreateInternalNotificationCommandHandler(INotificationService NotificationService) : IInterceptable, ICommandHandler<CreateInternalNotificationCommand>
{
    private readonly INotificationService _notificationService = NotificationService;

    /// <inheritdoc/>
    public async Task<Response> Handle(CreateInternalNotificationCommand request, CancellationToken cancellationToken)
    {
        await _notificationService.PublishAsync(request, userExpression: null, cancellationToken);

        return Response.Success();
    }
}
