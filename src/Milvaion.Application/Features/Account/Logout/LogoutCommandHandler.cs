using Microsoft.AspNetCore.Http;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Ef.Transaction;

namespace Milvaion.Application.Features.Account.Logout;

/// <summary>
/// Handles the LogoutCommand and performs the logout operation.
/// </summary>
[Transaction]
public record LogoutCommandHandler(IMilvaionRepositoryBase<UserSession> UserSessionRepository,
                                   IMilvaionRepositoryBase<UserSessionHistory> UserSessionHistoriesRepository,
                                   IAccountManager AccountManager,
                                   IHttpContextAccessor HttpContextAccessor) : IInterceptable, ICommandHandler<LogoutCommand>
{
    private readonly IMilvaionRepositoryBase<UserSession> _userSessionRepository = UserSessionRepository;
    private readonly IMilvaionRepositoryBase<UserSessionHistory> _userSessionHistoriesRepository = UserSessionHistoriesRepository;
    private readonly IHttpContextAccessor _httpContextAccessor = HttpContextAccessor;

    /// <inheritdoc/>
    public async Task<Response> Handle(LogoutCommand request, CancellationToken cancellationToken)
    {
        var currentSession = await _userSessionRepository.GetFirstOrDefaultAsync(UserSession.Conditions.CurrentSession(request.UserName, request.DeviceId),
                                                                                 UserSession.Projections.CurrentSession,
                                                                                 cancellationToken: cancellationToken);

        var currentToken = _httpContextAccessor.GetTokenFromHeader();

        if (currentSession == null || currentSession.AccessToken != currentToken || !_httpContextAccessor.IsCurrentUser(currentSession.UserName))
            return Response.Error(MessageKey.Unauthorized);

        await _userSessionRepository.DeleteAsync(currentSession, cancellationToken);

        await _userSessionHistoriesRepository.AddAsync(new UserSessionHistory(currentSession), cancellationToken);

        return Response.Success();
    }
}
