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
[Log]
[Transaction]
[UserActivityTrack(UserActivity.UpdateRole)]
public record UpdateRoleCommandHandler(IMilvaionRepositoryBase<Role> RoleRepository,
                                       IMilvaionRepositoryBase<RolePermissionRelation> RolePermissionRelationRepository) : IInterceptable, ICommandHandler<UpdateRoleCommand, int>
{
    private readonly IMilvaionRepositoryBase<Role> _roleRepository = RoleRepository;
    private readonly IMilvaionRepositoryBase<RolePermissionRelation> _rolePermissionRelationRepository = RolePermissionRelationRepository;

    /// <inheritdoc/>
    public async Task<Response<int>> Handle(UpdateRoleCommand request, CancellationToken cancellationToken)
    {
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
        }

        return Response<int>.Success(request.Id);
    }
}
