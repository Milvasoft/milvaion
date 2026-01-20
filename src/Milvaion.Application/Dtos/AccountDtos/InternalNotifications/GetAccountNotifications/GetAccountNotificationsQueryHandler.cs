using Microsoft.AspNetCore.Http;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Enums;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Dtos.AccountDtos.InternalNotifications.GetAccountNotifications;

/// <summary>
/// Handles the query for retrieving the account details.
/// </summary>
public class GetAccountNotificationsQueryHandler(IMilvaionRepositoryBase<User> userRepository,
                                                 IMilvaionRepositoryBase<InternalNotification> notificationRepository,
                                                 IHttpContextAccessor httpContextAccessor) : IInterceptable, IListQueryHandler<GetAccountNotificationsQuery, AccountNotificationDto>
{
    private readonly IMilvaionRepositoryBase<User> _userRepository = userRepository;
    private readonly IMilvaionRepositoryBase<InternalNotification> _notificationRepository = notificationRepository;
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;

    /// <inheritdoc/>
    public async Task<ListResponse<AccountNotificationDto>> Handle(GetAccountNotificationsQuery request, CancellationToken cancellationToken)
    {
        var user = await _userRepository.GetByIdAsync(request.UserId, projection: User.Projections.CurrentUserCheck, cancellationToken: cancellationToken);

        if (user == null)
            return ListResponse<AccountNotificationDto>.Success(default, MessageKey.UserNotFound, MessageType.Warning);

        if (!_httpContextAccessor.IsCurrentUser(user.UserName))
            return ListResponse<AccountNotificationDto>.Error(default, MessageKey.Unauthorized);

        var notifications = await _notificationRepository.GetAllAsync(request,
                                                                      condition: n => n.RecipientUserName == user.UserName,
                                                                      projection: AccountNotificationDto.Projection,
                                                                      cancellationToken: cancellationToken);

        return notifications;
    }
}
