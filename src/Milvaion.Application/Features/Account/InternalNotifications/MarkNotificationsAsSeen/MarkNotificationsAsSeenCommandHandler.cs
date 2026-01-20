using Microsoft.AspNetCore.Http;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.Account.InternalNotifications.MarkNotificationsAsSeen;

/// <summary>
/// Handles the RefreshLoginCommand and refreshes the user's login token.
/// </summary>
public record MarkNotificationsAsSeenCommandHandler(INotificationService NotificationService, IHttpContextAccessor HttpContextAccessor) : IInterceptable, ICommandHandler<MarkNotificationsAsSeenCommand>
{
    private readonly INotificationService _notificationService = NotificationService;
    private readonly IHttpContextAccessor _httpContextAccessor = HttpContextAccessor;

    /// <inheritdoc/>
    public async Task<Response> Handle(MarkNotificationsAsSeenCommand request, CancellationToken cancellationToken)
    {
        if (request.MarkAll)
            await _notificationService.MarkAllAsSeenAsync(_httpContextAccessor.HttpContext.CurrentUserName(), cancellationToken);
        else
            await _notificationService.MarkAsSeenAsync(request.NotificationIdList, _httpContextAccessor.HttpContext.CurrentUserName(), cancellationToken);

        return Response.Success();
    }
}
