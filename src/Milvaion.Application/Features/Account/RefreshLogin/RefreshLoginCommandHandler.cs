using Microsoft.AspNetCore.Http;
using Milvaion.Application.Dtos.AccountDtos;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Ef.Transaction;

namespace Milvaion.Application.Features.Account.RefreshLogin;

/// <summary>
/// Handles the RefreshLoginCommand and refreshes the user's login token.
/// </summary>
[Transaction]
public record RefreshLoginCommandHandler(IMilvaionRepositoryBase<User> UserRepository,
                                         IAccountManager AccountManager,
                                         IHttpContextAccessor HttpContextAccessor) : IInterceptable, ICommandHandler<RefreshLoginCommand, LoginResponseDto>
{
    private readonly IMilvaionRepositoryBase<User> _userRepository = UserRepository;
    private readonly IAccountManager _accountManager = AccountManager;
    private readonly IHttpContextAccessor _httpContextAccessor = HttpContextAccessor;

    /// <inheritdoc/>
    public async Task<Response<LoginResponseDto>> Handle(RefreshLoginCommand request, CancellationToken cancellationToken)
    {
        var user = await _userRepository.GetFirstOrDefaultAsync(i => i.UserName == request.UserName, User.Projections.Login, cancellationToken: cancellationToken);

        if (user == null)
            return Response<LoginResponseDto>.Error(null, MessageKey.Unauthorized);

        var relatedSession = user.Sessions.Find(s => s.RefreshToken == request.RefreshToken && s.DeviceId == request.DeviceId);

        var currentToken = _httpContextAccessor.GetTokenFromHeader();

        if (relatedSession == null || relatedSession.AccessToken != currentToken)
            return Response<LoginResponseDto>.Error(null, MessageKey.Unauthorized);

        if (relatedSession.ExpiryDate < DateTime.UtcNow)
            return Response<LoginResponseDto>.Error(null, MessageKey.SessionTimeout);

        var tokenModel = await _accountManager.LoginAsync(user, request.DeviceId, cancellationToken);

        var loginResponse = new LoginResponseDto
        {
            Id = user.Id,
            UserType = user.UserType,
            Token = tokenModel
        };

        return Response<LoginResponseDto>.Success(loginResponse);
    }
}
