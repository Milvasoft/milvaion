using Mapster;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.Roles.CreateRole;

/// <summary>
/// Handles the creation of the role.
/// </summary>
/// <param name="RoleRepository"></param>
/// <param name="PrivilegeGuard"></param>
[Log]
[UserActivityTrack(UserActivity.CreateRole)]
public record CreateRoleCommandHandler(IMilvaionRepositoryBase<Role> RoleRepository, IPrivilegeGuard PrivilegeGuard) : IInterceptable, ICommandHandler<CreateRoleCommand, int>
{
    private readonly IMilvaionRepositoryBase<Role> _roleRepository = RoleRepository;
    private readonly IPrivilegeGuard _privilegeGuard = PrivilegeGuard;

    /// <inheritdoc/>
    public async Task<Response<int>> Handle(CreateRoleCommand request, CancellationToken cancellationToken)
    {
        await _privilegeGuard.EnsureCanGrantPermissionsAsync(request.PermissionIdList, cancellationToken);

        var role = request.Adapt<Role>();

        role.RolePermissionRelations = request.PermissionIdList?.Select(permissionId => new RolePermissionRelation { PermissionId = permissionId }).ToList();

        await _roleRepository.AddAsync(role, cancellationToken);

        return Response<int>.Success(role.Id);
    }
}
