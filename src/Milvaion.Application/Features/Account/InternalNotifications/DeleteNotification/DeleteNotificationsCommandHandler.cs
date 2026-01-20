using Microsoft.AspNetCore.Http;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.Account.InternalNotifications.DeleteNotification;

/// <summary>
/// Handles the RefreshLoginCommand and refreshes the user's login token.
/// </summary>
public record DeleteNotificationsCommandHandler(INotificationService NotificationService, IHttpContextAccessor HttpContextAccessor) : IInterceptable, ICommandHandler<DeleteNotificationsCommand>
{
    private readonly INotificationService _notificationService = NotificationService;
    private readonly IHttpContextAccessor _httpContextAccessor = HttpContextAccessor;

    /// <inheritdoc/>
    public async Task<Response> Handle(DeleteNotificationsCommand request, CancellationToken cancellationToken)
    {
        if (request.DeleteAll)
            await _notificationService.DeleteAllAsync(_httpContextAccessor.HttpContext.CurrentUserName(), cancellationToken);
        else
            await _notificationService.DeleteAsync(request.NotificationIdList, _httpContextAccessor.HttpContext.CurrentUserName(), cancellationToken);

        return Response.Success();
    }
}
