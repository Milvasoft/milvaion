using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.Roles.DeleteRole;

/// <summary>
/// Handles the deletion of the role.
/// </summary>
/// <param name="RoleRepository"></param>
/// <param name="ExternalIdentityService"></param>
[Log]
[UserActivityTrack(UserActivity.DeleteRole)]
public record DeleteRoleCommandHandler(IMilvaionRepositoryBase<Role> RoleRepository, IExternalIdentityService ExternalIdentityService) : IInterceptable, ICommandHandler<DeleteRoleCommand, int>
{
    private readonly IMilvaionRepositoryBase<Role> _roleRepository = RoleRepository;
    private readonly IExternalIdentityService _externalIdentityService = ExternalIdentityService;

    /// <inheritdoc/>
    public async Task<Response<int>> Handle(DeleteRoleCommand request, CancellationToken cancellationToken)
    {
        var role = await _roleRepository.GetForDeleteAsync(request.RoleId, cancellationToken: cancellationToken);

        if (role == null)
            return Response<int>.Error(0, MessageKey.RoleNotFound);

        await _roleRepository.DeleteAsync(role, cancellationToken: cancellationToken);

        // A removed role changes what external users are granted; drop the external-identity cache so the change takes effect immediately instead of lingering until the cache expires.
        await _externalIdentityService.InvalidateAllAsync(cancellationToken);

        return Response<int>.Success(request.RoleId);
    }
}
