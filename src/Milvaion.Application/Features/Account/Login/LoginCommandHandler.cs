using Milvaion.Application.Dtos.AccountDtos;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Abstractions.Localization;
using Milvasoft.Identity.Abstract;
using Milvasoft.Identity.Concrete.Options;
using Milvasoft.Interception.Ef.Transaction;

namespace Milvaion.Application.Features.Account.Login;

/// <summary>
/// Handles the login command and performs the necessary operations.
/// </summary>
[Transaction]
public record LoginCommandHandler(IMilvaionRepositoryBase<User> UserRepository,
                                  IMilvaUserManager<User, int> MilvaUserManager,
                                  IAccountManager AccountManager,
                                  IUIService UIService,
                                  IMilvaLocalizer MilvaLocalizer,
                                  MilvaIdentityOptions IdentityOptions) : IInterceptable, ICommandHandler<LoginCommand, LoginResponseDto>
{
    private readonly IMilvaionRepositoryBase<User> _userRepository = UserRepository;
    private readonly IMilvaUserManager<User, int> _milvaUserManager = MilvaUserManager;
    private readonly IAccountManager _accountManager = AccountManager;
    private readonly IUIService _uiService = UIService;
    private readonly IMilvaLocalizer _milvaLocalizer = MilvaLocalizer;
    private readonly MilvaIdentityOptions _identityOptions = IdentityOptions;

    /// <inheritdoc/>
    public async Task<Response<LoginResponseDto>> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        var user = await _userRepository.GetFirstOrDefaultAsync(i => i.UserName == request.UserName, User.Projections.Login, cancellationToken: cancellationToken);

        if (user == null)
            return Response<LoginResponseDto>.Error(null, MessageKey.Unauthorized);

        var lockoutResponse = ValidateLockout(user);

        if (lockoutResponse != null)
            return lockoutResponse;

        // If pass incorrect configures lockout and returns error message.
        var passwordCheckResponse = await CheckPasswordAsync(user, request.Password, cancellationToken);

        if (passwordCheckResponse != null)
            return passwordCheckResponse;

        var tokenModel = await _accountManager.LoginAsync(user, request.DeviceId, cancellationToken);

        var loginResponse = new LoginResponseDto
        {
            Id = user.Id,
            UserType = user.UserType,
            Token = tokenModel
        };

        var permissions = user.RoleRelations.SelectMany(i => i.Role.RolePermissionRelations.Select(i => i.Permission));

        loginResponse.AccessibleMenuItems = await _uiService.GetAccessibleMenuItemsAsync(permissions, cancellationToken);
        loginResponse.PageInformations = await _uiService.GetPagesAccessibilityAsync(permissions.Select(p => p.FormatPermissionAndGroup()), cancellationToken);

        return Response<LoginResponseDto>.Success(loginResponse);
    }

    private Response<LoginResponseDto> ValidateLockout(User user)
    {
        var userLocked = _milvaUserManager.IsLockedOut(user);

        // If the user is locked out and the unlock date has passed.
        if (userLocked && DateTime.UtcNow > user.LockoutEnd.Value.DateTime)
        {
            //We reset the duration of the locked user.
            _milvaUserManager.ConfigureLockout(user, false);

            userLocked = false;
        }

        if (userLocked)
            return PrepareLockoutResponse(user);

        return null;
    }

    private Response<LoginResponseDto> PrepareLockoutResponse(User user)
    {
        var remainingLockoutEnd = user.LockoutEnd - DateTime.UtcNow;

        string message;

        if (remainingLockoutEnd.Value.Hours > 0)
            message = _milvaLocalizer[MessageKey.Locked, _milvaLocalizer[MessageKey.Hours, remainingLockoutEnd.Value.Hours]];
        else if (remainingLockoutEnd.Value.Minutes > 0)
            message = _milvaLocalizer[MessageKey.Locked, _milvaLocalizer[MessageKey.Minutes, remainingLockoutEnd.Value.Minutes]];
        else
            message = _milvaLocalizer[MessageKey.Locked, _milvaLocalizer[MessageKey.Seconds, remainingLockoutEnd.Value.Seconds]];

        return Response<LoginResponseDto>.Error(null, message);
    }

    private async Task<Response<LoginResponseDto>> CheckPasswordAsync(User user, string password, CancellationToken cancellationToken = default)
    {
        var isPasswordTrue = _milvaUserManager.CheckPassword(user, password);

        Response<LoginResponseDto> response = null;

        if (!isPasswordTrue)
        {
            if (user.LockoutEnabled)
            {
                _milvaUserManager.ConfigureLockout(user, true);

                if (_milvaUserManager.IsLockedOut(user))
                {
                    response = PrepareLockoutResponse(user);
                }
                else
                {
                    var lockWarningMessage = _milvaLocalizer[MessageKey.LockWarning, _identityOptions.Lockout.MaxFailedAccessAttempts - user.AccessFailedCount];

                    response = Response<LoginResponseDto>.Error(null, lockWarningMessage);
                }
            }
            else
                response = Response<LoginResponseDto>.Error(null, MessageKey.Unauthorized);
        }
        else
        {
            user.AccessFailedCount = 0;
            user.LockoutEnd = null;
        }

        if (user.UserName != GlobalConstant.RootUsername)
            await _userRepository.UpdateAsync(user, cancellationToken, i => i.AccessFailedCount, i => i.LockoutEnd);

        return response;
    }
}