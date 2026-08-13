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
                                  MilvaIdentityOptions IdentityOptions,
                                  MilvaionConfig MilvaionConfig,
                                  ILdapAuthenticator LdapAuthenticator,
                                  IExternalIdentityService ExternalIdentityService) : IInterceptable, ICommandHandler<LoginCommand, LoginResponseDto>
{
    private readonly IMilvaionRepositoryBase<User> _userRepository = UserRepository;
    private readonly IMilvaUserManager<User, int> _milvaUserManager = MilvaUserManager;
    private readonly IAccountManager _accountManager = AccountManager;
    private readonly IUIService _uiService = UIService;
    private readonly IMilvaLocalizer _milvaLocalizer = MilvaLocalizer;
    private readonly MilvaIdentityOptions _identityOptions = IdentityOptions;
    private readonly MilvaionConfig _milvaionConfig = MilvaionConfig;
    private readonly ILdapAuthenticator _ldapAuthenticator = LdapAuthenticator;
    private readonly IExternalIdentityService _externalIdentityService = ExternalIdentityService;

    /// <inheritdoc/>
    public async Task<Response<LoginResponseDto>> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        var user = await _userRepository.GetFirstOrDefaultAsync(i => i.UserName == request.UserName, User.Projections.Login, cancellationToken: cancellationToken);

        // Directory-backed users verify their password against LDAP, not the local hash. This also covers the first login, when no shadow record exists yet.
        if (_milvaionConfig?.Authentication?.Ldap?.Enabled == true && (user is null || user.Provider == ExternalProvider.Ldap))
            return await HandleLdapLoginAsync(request, cancellationToken);

        if (user == null)
            return Response<LoginResponseDto>.Error(null, MessageKey.Unauthorized);

        // Externally authenticated users (OIDC/LDAP) have no local password hash. LDAP is handled above; any
        // other external user must sign in through its provider, so reject rather than crashing the hasher.
        if (user.Provider != ExternalProvider.Local)
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

    /// <summary>
    /// Authenticates against LDAP/Active Directory, provisions or refreshes the shadow user and its
    /// roles, then issues Milvaion's own token exactly as a local login would. The directory owns the
    /// identity and group membership; the permissions of those roles are assigned in Milvaion.
    /// </summary>
    private async Task<Response<LoginResponseDto>> HandleLdapLoginAsync(LoginCommand request, CancellationToken cancellationToken)
    {
        var ldapResult = await _ldapAuthenticator.AuthenticateAsync(request.UserName, request.Password, cancellationToken);

        if (!ldapResult.Success)
            return Response<LoginResponseDto>.Error(null, MessageKey.Unauthorized);

        var descriptor = new ExternalIdentityDescriptor
        {
            Provider = ExternalProvider.Ldap,
            Issuer = _milvaionConfig.Authentication.Ldap.Host,
            Subject = ldapResult.Subject,
            UserName = request.UserName,
            Email = ldapResult.Email,
            Name = ldapResult.Name,
            Surname = ldapResult.Surname,
            RoleNames = ldapResult.Groups
        };

        await _externalIdentityService.ResolveAndBuildClaimsAsync(descriptor, cancellationToken);

        var user = await _userRepository.GetFirstOrDefaultAsync(u => u.Issuer == descriptor.Issuer && u.ExternalSubject == descriptor.Subject,
                                                                User.Projections.Login,
                                                                cancellationToken: cancellationToken);

        if (user is null)
            return Response<LoginResponseDto>.Error(null, MessageKey.Unauthorized);

        var tokenModel = await _accountManager.LoginAsync(user, request.DeviceId, cancellationToken);

        var loginResponse = new LoginResponseDto
        {
            Id = user.Id,
            Token = tokenModel
        };

        var permissions = user.RoleRelations.SelectMany(i => i.Role.RolePermissionRelations.Select(i => i.Permission));

        loginResponse.AccessibleMenuItems = await _uiService.GetAccessibleMenuItemsAsync(permissions, cancellationToken);
        loginResponse.PageInformations = await _uiService.GetPagesAccessibilityAsync(permissions.Select(p => p.FormatPermissionAndGroup()), cancellationToken);

        return Response<LoginResponseDto>.Success(loginResponse);
    }
}