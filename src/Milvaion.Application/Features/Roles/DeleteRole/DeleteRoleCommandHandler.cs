using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.Roles.DeleteRole;

/// <summary>
/// Handles the deletion of the role.
/// </summary>
/// <param name="RoleRepository"></param>
[Log]
[UserActivityTrack(UserActivity.DeleteRole)]
public record DeleteRoleCommandHandler(IMilvaionRepositoryBase<Role> RoleRepository) : IInterceptable, ICommandHandler<DeleteRoleCommand, int>
{
    private readonly IMilvaionRepositoryBase<Role> _roleRepository = RoleRepository;

    /// <inheritdoc/>
    public async Task<Response<int>> Handle(DeleteRoleCommand request, CancellationToken cancellationToken)
    {
        var role = await _roleRepository.GetForDeleteAsync(request.RoleId, cancellationToken: cancellationToken);

        if (role == null)
            return Response<int>.Error(0, MessageKey.RoleNotFound);

        await _roleRepository.DeleteAsync(role, cancellationToken: cancellationToken);

        return Response<int>.Success(request.RoleId);
    }
}
