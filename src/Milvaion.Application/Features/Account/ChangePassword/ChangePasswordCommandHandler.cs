using Microsoft.AspNetCore.Http;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Identity.Abstract;
using Milvasoft.Identity.Concrete;
using Milvasoft.Interception.Ef.Transaction;

namespace Milvaion.Application.Features.Account.ChangePassword;

/// <summary>
/// Handles the change password command.
/// </summary>
[Transaction]
public record ChangePasswordCommandHandler(IMilvaionRepositoryBase<User> UserRepository,
                                           IMilvaionRepositoryBase<UserSession> UserSessionRepository,
                                           IMilvaionRepositoryBase<UserSessionHistory> UserSessionHistoriesRepository,
                                           IMilvaUserManager<User, int> MilvaUserManager,
                                           IHttpContextAccessor HttpContextAccessor) : IInterceptable, ICommandHandler<ChangePasswordCommand>
{
    private readonly IMilvaionRepositoryBase<User> _userRepository = UserRepository;
    private readonly IMilvaUserManager<User, int> _milvaUserManager = MilvaUserManager;
    private readonly IHttpContextAccessor _httpContextAccessor = HttpContextAccessor;
    private readonly IMilvaionRepositoryBase<UserSession> _userSessionRepository = UserSessionRepository;
    private readonly IMilvaionRepositoryBase<UserSessionHistory> _userSessionHistoriesRepository = UserSessionHistoriesRepository;

    /// <inheritdoc/>
    public async Task<Response> Handle(ChangePasswordCommand request, CancellationToken cancellationToken)
    {
        var user = await _userRepository.GetFirstOrDefaultAsync(i => i.UserName == request.UserName, User.Projections.ChangePassword, cancellationToken: cancellationToken);

        if (user == null || !_httpContextAccessor.IsCurrentUser(user.UserName))
            return Response<MilvaToken>.Error(null, MessageKey.Unauthorized);

        if (!_milvaUserManager.CheckPassword(user, request.OldPassword))
            return Response<MilvaToken>.Error(null, MessageKey.WrongPassword);

        _milvaUserManager.ValidateAndSetPasswordHash(user, request.NewPassword);

        await _userRepository.UpdateAsync(user, cancellationToken, u => u.PasswordHash);

        await _userSessionRepository.ExecuteDeleteAsync(UserSession.Conditions.DeleteAllSessions(user.UserName), cancellationToken: cancellationToken);

        await _userSessionHistoriesRepository.BulkAddAsync([.. user.Sessions?.Select(s => new UserSessionHistory(s))], cancellationToken: cancellationToken);

        return Response.Success();
    }
}
