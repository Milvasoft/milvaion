using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Ef.Transaction;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.Roles.UpdateRole;

/// <summary>
/// Handles the update of the role.
/// </summary>
/// <param name="RoleRepository"></param>
/// <param name="RolePermissionRelationRepository"></param>
/// <param name="ExternalIdentityService"></param>
[Log]
[Transaction]
[UserActivityTrack(UserActivity.UpdateRole)]
public record UpdateRoleCommandHandler(IMilvaionRepositoryBase<Role> RoleRepository,
                                       IMilvaionRepositoryBase<RolePermissionRelation> RolePermissionRelationRepository,
                                       IExternalIdentityService ExternalIdentityService) : IInterceptable, ICommandHandler<UpdateRoleCommand, int>
{
    private readonly IMilvaionRepositoryBase<Role> _roleRepository = RoleRepository;
    private readonly IMilvaionRepositoryBase<RolePermissionRelation> _rolePermissionRelationRepository = RolePermissionRelationRepository;
    private readonly IExternalIdentityService _externalIdentityService = ExternalIdentityService;

    /// <inheritdoc/>
    public async Task<Response<int>> Handle(UpdateRoleCommand request, CancellationToken cancellationToken)
    {
        // Externally owned roles mirror a provider group: the name is owned there. The UI always sends the name with isUpdated=true, so instead of rejecting we simply ignore the name change and let only the permission set be updated here.
        if (request.Name.IsUpdated)
        {
            var existing = await _roleRepository.GetByIdAsync(request.Id, projection: r => new Role { Id = r.Id, Provider = r.Provider }, cancellationToken: cancellationToken);

            if (existing is not null && existing.Provider != ExternalProvider.Local)
                request.Name = default;
        }

        var setPropertyBuilder = _roleRepository.GetUpdatablePropertiesBuilder(request);

        await _roleRepository.ExecuteUpdateAsync(request.Id, setPropertyBuilder, cancellationToken: cancellationToken);

        if (request.PermissionIdList.IsUpdated)
        {
            await _rolePermissionRelationRepository.ExecuteDeleteAsync(rl => rl.RoleId == request.Id, cancellationToken: cancellationToken);

            var addedEntities = request.PermissionIdList.Value?.Distinct()
                                                               .Select(permissionId => new RolePermissionRelation { RoleId = request.Id, PermissionId = permissionId })
                                                               .ToList();

            if (addedEntities?.Count > 0)
                await _rolePermissionRelationRepository.BulkAddAsync(addedEntities, null, cancellationToken);

            // The role's permission set changed, so any external user carrying this role has a stale cached
            // claim set. Drop the external-identity cache so they pick up the new permissions immediately.
            await _externalIdentityService.InvalidateAllAsync(cancellationToken);
        }

        return Response<int>.Success(request.Id);
    }
}
