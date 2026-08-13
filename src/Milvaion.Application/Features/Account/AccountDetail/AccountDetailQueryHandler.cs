using Microsoft.AspNetCore.Http;
using Milvaion.Application.Dtos.AccountDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Enums;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.Account.AccountDetail;

/// <summary>
/// Handles the query for retrieving the account details.
/// </summary>
public class AccountDetailQueryHandler(IMilvaionRepositoryBase<User> userRepository,
                                       IHttpContextAccessor httpContextAccessor) : IInterceptable, IQueryHandler<AccountDetailQuery, AccountDetailDto>
{
    private readonly IMilvaionRepositoryBase<User> _userRepository = userRepository;
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;

    /// <inheritdoc/>
    public async Task<Response<AccountDetailDto>> Handle(AccountDetailQuery request, CancellationToken cancellationToken)
    {
        // This endpoint only ever returns the caller's own account. SSO/OIDC users never hit /account/login,
        // so the browser has no local user id to send: when it is omitted, resolve the account from the authenticated identity instead of the query.
        AccountDetailDto user;

        if (request.UserId > 0)
        {
            user = await _userRepository.GetByIdAsync(request.UserId, projection: AccountDetailDto.Projection, cancellationToken: cancellationToken);
        }
        else
        {
            var currentUserName = _httpContextAccessor.HttpContext?.User?.Identity?.Name;

            if (string.IsNullOrWhiteSpace(currentUserName))
                return Response<AccountDetailDto>.Error(default, MessageKey.Unauthorized);

            user = await _userRepository.GetFirstOrDefaultAsync(u => u.UserName == currentUserName, projection: AccountDetailDto.Projection, cancellationToken: cancellationToken);
        }

        if (user == null)
            return Response<AccountDetailDto>.Success(default, MessageKey.UserNotFound, MessageType.Warning);

        if (!_httpContextAccessor.IsCurrentUser(user.UserName))
            return Response<AccountDetailDto>.Error(default, MessageKey.Unauthorized);

        return Response<AccountDetailDto>.Success(user);
    }
}
