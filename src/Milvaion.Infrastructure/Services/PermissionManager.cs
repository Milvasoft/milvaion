using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.PermissionManager;
using Milvaion.Domain;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Helpers;
using Milvasoft.Interception.Ef.Transaction;

namespace Milvaion.Infrastructure.Services;

/// <summary>
/// Permission manager for managing system permissions.
/// </summary>
public class PermissionManager(IMilvaionRepositoryBase<Permission> permissionRepository, IMilvaionRepositoryBase<RolePermissionRelation> rolePermissionRelationRepository) : IPermissionManager
{
    private readonly IMilvaionRepositoryBase<Permission> _permissionRepository = permissionRepository;
    private readonly IMilvaionRepositoryBase<RolePermissionRelation> _rolePermissionRelationRepository = rolePermissionRelationRepository;

    /// <summary>
    /// Get all permissions as entity.
    /// </summary>
    /// <returns></returns>
    public async Task<List<Permission>> GetAllPermissionsAsync(CancellationToken cancellationToken)
    {
        var permissions = await _permissionRepository.GetAllAsync(projection: i => new Permission
        {
            Id = i.Id,
            Name = i.Name,
            NormalizedName = i.NormalizedName,
            Description = i.Description,
            PermissionGroup = i.PermissionGroup,
            PermissionGroupDescription = i.PermissionGroupDescription,
            RolePermissionRelations = i.RolePermissionRelations,
        }, cancellationToken: cancellationToken);

        return permissions;
    }

    /// <summary>
    /// Migrate permissions to the database.
    /// </summary>
    /// <returns></returns>
    [Transaction]
    public async Task<Response<string>> MigratePermissionsAsync(CancellationToken cancellationToken)
    {
        var permissions = await GetAllPermissionsAsync(cancellationToken);

        var groupedPermissionsInDatabase = permissions.GroupBy(p => p.PermissionGroup).ToDictionary(g => g.Key, g => g.ToList());

        var groupedStaticPermissions = PermissionCatalog.GetPermissionsAndGroups();

        List<Permission> permissionsToAdd = [];
        List<Permission> permissionsToRemove = [];

        foreach (var staticGroupPermissionsPair in groupedStaticPermissions)
        {
            var groupExistsInDatabase = groupedPermissionsInDatabase.TryGetValue(staticGroupPermissionsPair.Key, out var permissionsInDatabase);

            var staticPermissionsInGroup = staticGroupPermissionsPair.Value;

            if (!groupExistsInDatabase)
            {
                permissionsToAdd.AddRange(staticPermissionsInGroup);
            }
            else
            {
                permissionsToAdd.AddRange(staticPermissionsInGroup.ExceptBy(permissionsInDatabase.Select(i => i.NormalizedName), i => i.NormalizedName));

                permissionsToRemove.AddRange(permissionsInDatabase.ExceptBy(staticPermissionsInGroup.Select(i => i.NormalizedName), i => i.NormalizedName));
            }
        }

        if (!groupedPermissionsInDatabase.IsNullOrEmpty())
        {
            var notExistsGroups = groupedPermissionsInDatabase.ExceptBy(groupedStaticPermissions.Keys, i => i.Key);

            permissionsToRemove.AddRange(notExistsGroups.SelectMany(i => i.Value));
        }

        // Add the permissions to the database
        if (permissionsToAdd.Count > 0)
            await _permissionRepository.BulkAddAsync(permissionsToAdd, cancellationToken: cancellationToken);

        if (permissionsToRemove.Count > 0)
        {
            // Remove the permissions role relations from the database
            var roleRelations = permissionsToRemove.SelectMany(i => i.RolePermissionRelations).ToList();

            await _rolePermissionRelationRepository.BulkDeleteAsync(roleRelations, cancellationToken: cancellationToken);

            await _permissionRepository.BulkDeleteAsync(permissionsToRemove, cancellationToken: cancellationToken);
        }

        permissions = await GetAllPermissionsAsync(cancellationToken);

        foreach (var permission in permissions)
            PermissionCatalog.Permissions.Add(permission);

        return Response<string>.Success($"Added : {permissionsToAdd.Count} / Removed : {permissionsToRemove.Count}");
    }
}
