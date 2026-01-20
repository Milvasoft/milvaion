using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Identity.Abstract;
using Milvasoft.Identity.Concrete.Options;
using Milvasoft.Interception.Ef.Transaction;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.Users.UpdateUser;

/// <summary>
/// Handles the update of the user.
/// </summary>
/// <param name="UserRepository"></param>
/// <param name="UserRoleRelationRepository"></param>
/// <param name="UserSessionRepository"></param>
/// <param name="UserSessionHistoriesRepository"></param>
/// <param name="MilvaIdentityOptions"></param>
/// <param name="MilvaUserManager"></param>
/// <param name="MilvaPasswordHasher"></param>
[Log]
[Transaction]
[UserActivityTrack(UserActivity.UpdateUser)]
public record UpdateUserCommandHandler(IMilvaionRepositoryBase<User> UserRepository,
                                       IMilvaionRepositoryBase<UserRoleRelation> UserRoleRelationRepository,
                                       IMilvaionRepositoryBase<UserSession> UserSessionRepository,
                                       IMilvaionRepositoryBase<UserSessionHistory> UserSessionHistoriesRepository,
                                       Lazy<MilvaIdentityOptions> MilvaIdentityOptions,
                                       Lazy<IMilvaUserManager<User, int>> MilvaUserManager,
                                       Lazy<IMilvaPasswordHasher> MilvaPasswordHasher) : IInterceptable, ICommandHandler<UpdateUserCommand, int>
{
    private readonly IMilvaionRepositoryBase<User> _userRepository = UserRepository;
    private readonly IMilvaionRepositoryBase<UserRoleRelation> _userRoleRelationRepository = UserRoleRelationRepository;
    private readonly IMilvaionRepositoryBase<UserSession> _userSessionRepository = UserSessionRepository;
    private readonly IMilvaionRepositoryBase<UserSessionHistory> _userSessionHistoriesRepository = UserSessionHistoriesRepository;
    private readonly Lazy<MilvaIdentityOptions> _milvaIdentityOptions = MilvaIdentityOptions;
    private readonly Lazy<IMilvaPasswordHasher> _milvaPasswordHasher = MilvaPasswordHasher;
    private readonly Lazy<IMilvaUserManager<User, int>> _milvaUserManager = MilvaUserManager;

    /// <inheritdoc/>
    public async Task<Response<int>> Handle(UpdateUserCommand request, CancellationToken cancellationToken)
    {
        var setPropertyBuilder = _userRepository.GetUpdatablePropertiesBuilder(request);

        if (request.Lockout.IsUpdated)
        {
            if (request.Lockout.Value)
                setPropertyBuilder.SetPropertyValue(u => u.LockoutEnd, DateTimeOffset.Now.AddMinutes(_milvaIdentityOptions.Value.Lockout.DefaultLockoutMinute));
            else
                setPropertyBuilder.SetPropertyValue(u => u.LockoutEnd, null).SetPropertyValue(u => u.AccessFailedCount, 0);
        }

        if (request.NewPassword.IsUpdated)
        {
            var errorMessage = _milvaUserManager.Value.ValidatePassword(request.NewPassword.Value);

            if (errorMessage != null)
                return Response<int>.Error(0, errorMessage);

            var hashedPassword = _milvaPasswordHasher.Value.HashPassword(request.NewPassword.Value);

            setPropertyBuilder.SetPropertyValue(u => u.PasswordHash, hashedPassword);
        }

        await _userRepository.ExecuteUpdateAsync(request.Id, setPropertyBuilder, cancellationToken: cancellationToken);

        if (request.RoleIdList.IsUpdated)
        {
            await _userRoleRelationRepository.ExecuteDeleteAsync(rl => rl.UserId == request.Id, cancellationToken: cancellationToken);

            var addedEntities = request.RoleIdList.Value?.Distinct()
                                                         .Select(roleId => new UserRoleRelation { RoleId = roleId, UserId = request.Id })
                                                         .ToList();

            if (addedEntities?.Count > 0)
                await _userRoleRelationRepository.BulkAddAsync(addedEntities, null, cancellationToken);

            var user = await _userRepository.GetByIdAsync(request.Id, projection: User.Projections.UpdateUserWithSessions, cancellationToken: cancellationToken);

            await _userSessionRepository.ExecuteDeleteAsync(UserSession.Conditions.DeleteAllSessions(user.UserName), cancellationToken: cancellationToken);
            await _userSessionHistoriesRepository.BulkAddAsync([.. user.Sessions?.Select(s => new UserSessionHistory(s))], cancellationToken: cancellationToken);
        }

        return Response<int>.Success(request.Id);
    }
}
