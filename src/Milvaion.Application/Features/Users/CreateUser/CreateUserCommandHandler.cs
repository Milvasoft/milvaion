using Mapster;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Identity.Abstract;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.Users.CreateUser;

/// <summary>
/// Handles the creation of the user.
/// </summary>
/// <param name="UserRepository"></param>
/// <param name="UserManager"></param>
[Log]
[UserActivityTrack(UserActivity.CreateUser)]
public record CreateUserCommandHandler(IMilvaionRepositoryBase<User> UserRepository,
                                       IMilvaUserManager<User, int> UserManager) : IInterceptable, ICommandHandler<CreateUserCommand, int>
{
    private readonly IMilvaionRepositoryBase<User> _userRepository = UserRepository;
    private readonly IMilvaUserManager<User, int> _userManager = UserManager;

    /// <inheritdoc/>
    public async Task<Response<int>> Handle(CreateUserCommand request, CancellationToken cancellationToken)
    {
        var user = request.Adapt<User>();

        var errorMessageForUserName = _userManager.ValidateUserName(user.UserName);

        if (errorMessageForUserName is not null)
            return Response<int>.Error(default, errorMessageForUserName);

        var errorMessageForPass = _userManager.ValidatePassword(request.Password);

        if (errorMessageForPass is not null)
            return Response<int>.Error(default, errorMessageForPass);

        _userManager.ConfigureForCreate(ref user, request.Password);

        _userRepository.FetchSoftDeletedEntities(true);

        var userWithSameUsername = await _userRepository.GetFirstOrDefaultAsync(i => i.NormalizedUserName == user.NormalizedUserName, cancellationToken: cancellationToken);

        if (userWithSameUsername != null)
            return Response<int>.Error(0, MessageKey.UserExists);

        user.RoleRelations = request.RoleIdList?.Select(roleId => new UserRoleRelation { RoleId = roleId }).ToList();

        await _userRepository.AddAsync(user, cancellationToken);

        return Response<int>.Success(user.Id);
    }
}
