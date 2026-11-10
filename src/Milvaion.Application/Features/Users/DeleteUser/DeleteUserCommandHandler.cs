using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.Users.DeleteUser;

/// <summary>
/// Handles the deletion of the user.
/// </summary>
/// <param name="UserRepository"></param>
/// <param name="PrivilegeGuard"></param>
[Log]
[UserActivityTrack(UserActivity.DeleteUser)]
public record DeleteUserCommandHandler(IMilvaionRepositoryBase<User> UserRepository, IPrivilegeGuard PrivilegeGuard) : IInterceptable, ICommandHandler<DeleteUserCommand, int>
{
    private readonly IMilvaionRepositoryBase<User> _userRepository = UserRepository;
    private readonly IPrivilegeGuard _privilegeGuard = PrivilegeGuard;

    /// <inheritdoc/>
    public async Task<Response<int>> Handle(DeleteUserCommand request, CancellationToken cancellationToken)
    {
        await _privilegeGuard.EnsureCanManageUserAsync(request.UserId, cancellationToken);

        var user = await _userRepository.GetForDeleteAsync(request.UserId, cancellationToken: cancellationToken);

        if (user == null)
            return Response<int>.Error(0, MessageKey.UserNotFound);

        await _userRepository.DeleteAsync(user, cancellationToken: cancellationToken);

        return Response<int>.Success(request.UserId);
    }
}
