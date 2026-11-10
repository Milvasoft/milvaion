using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace Milvaion.Application.Utils.PermissionManager;

/// <inheritdoc cref="IPrivilegeGuard"/>
public class PrivilegeGuard(IHttpContextAccessor httpContextAccessor,
                            IMilvaionRepositoryBase<Permission> permissionRepository,
                            IMilvaionRepositoryBase<RolePermissionRelation> rolePermissionRepository,
                            IMilvaionRepositoryBase<UserRoleRelation> userRoleRepository,
                            IMilvaionRepositoryBase<MilvaionApiKeyPermissionRelation> apiKeyPermissionRepository) : IPrivilegeGuard
{
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly IMilvaionRepositoryBase<Permission> _permissionRepository = permissionRepository;
    private readonly IMilvaionRepositoryBase<RolePermissionRelation> _rolePermissionRepository = rolePermissionRepository;
    private readonly IMilvaionRepositoryBase<UserRoleRelation> _userRoleRepository = userRoleRepository;
    private readonly IMilvaionRepositoryBase<MilvaionApiKeyPermissionRelation> _apiKeyPermissionRepository = apiKeyPermissionRepository;

    private HttpContext Request => _httpContextAccessor.HttpContext;

    private bool IsExempt => Request is null || CallerPermissions.Contains(PermissionCatalog.App.SuperAdmin);

    private HashSet<string> CallerPermissions => [.. Request.User.FindAll(ClaimTypes.Role).Select(claim => claim.Value)];

    /// <inheritdoc/>
    public async Task EnsureCanGrantPermissionsAsync(IEnumerable<int> permissionIds, CancellationToken cancellationToken = default)
    {
        if (IsExempt)
            return;

        await EnsureCallerHoldsAsync(permissionIds, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task EnsureCanChangeRolePermissionsAsync(int roleId, IEnumerable<int> permissionIds, CancellationToken cancellationToken = default)
    {
        if (IsExempt)
            return;

        var current = await GetRolePermissionIdsAsync([roleId], cancellationToken);

        await EnsureCallerHoldsAsync((permissionIds ?? []).Except(current), cancellationToken);
    }

    /// <inheritdoc/>
    public async Task EnsureCanManageRoleAsync(int roleId, CancellationToken cancellationToken = default)
    {
        if (IsExempt)
            return;

        if (await ContainsSuperAdminAsync(await GetRolePermissionIdsAsync([roleId], cancellationToken), cancellationToken))
            Forbid();
    }

    /// <inheritdoc/>
    public async Task EnsureCanGrantRolesAsync(IEnumerable<int> roleIds, CancellationToken cancellationToken = default)
    {
        if (IsExempt)
            return;

        await EnsureCallerHoldsAsync(await GetRolePermissionIdsAsync([.. roleIds ?? []], cancellationToken), cancellationToken);
    }

    /// <inheritdoc/>
    public async Task EnsureCanChangeUserRolesAsync(int userId, IEnumerable<int> roleIds, CancellationToken cancellationToken = default)
    {
        if (IsExempt)
            return;

        var current = await GetUserRoleIdsAsync(userId, cancellationToken);

        await EnsureCanGrantRolesAsync((roleIds ?? []).Except(current), cancellationToken);
    }

    /// <inheritdoc/>
    public async Task EnsureCanManageUserAsync(int userId, CancellationToken cancellationToken = default)
    {
        if (IsExempt)
            return;

        var roleIds = await GetUserRoleIdsAsync(userId, cancellationToken);

        if (await ContainsSuperAdminAsync(await GetRolePermissionIdsAsync(roleIds, cancellationToken), cancellationToken))
            Forbid();
    }

    /// <inheritdoc/>
    public async Task EnsureCanGrantApiKeyPermissionsAsync(int? apiKeyId, IEnumerable<int> permissionIds, CancellationToken cancellationToken = default)
    {
        if (Request is null)
            return;

        var requested = (permissionIds ?? []).Distinct().ToList();

        if (await ContainsSuperAdminAsync(requested, cancellationToken))
            Forbid();

        if (IsExempt)
            return;

        var current = apiKeyId is null
            ? []
            : (await _apiKeyPermissionRepository.GetAllAsync(condition: rl => rl.MilvaionApiKeyId == apiKeyId.Value,
                                                             projection: rl => new MilvaionApiKeyPermissionRelation { PermissionId = rl.PermissionId },
                                                             cancellationToken: cancellationToken)).Select(rl => rl.PermissionId).ToList();

        await EnsureCallerHoldsAsync(requested.Except(current), cancellationToken);
    }

    private async Task EnsureCallerHoldsAsync(IEnumerable<int> permissionIds, CancellationToken cancellationToken)
    {
        var permissions = await GetPermissionsAsync(permissionIds, cancellationToken);
        var callerPermissions = CallerPermissions;

        if (!permissions.TrueForAll(permission => callerPermissions.Contains(permission.FormatPermissionAndGroup())))
            Forbid();
    }

    private async Task<bool> ContainsSuperAdminAsync(IEnumerable<int> permissionIds, CancellationToken cancellationToken)
    {
        var permissions = await GetPermissionsAsync(permissionIds, cancellationToken);

        return permissions.Exists(permission => permission.FormatPermissionAndGroup() == PermissionCatalog.App.SuperAdmin);
    }

    private async Task<List<Permission>> GetPermissionsAsync(IEnumerable<int> permissionIds, CancellationToken cancellationToken)
    {
        var ids = permissionIds?.Distinct().ToList() ?? [];

        if (ids.Count == 0)
            return [];

        return [.. await _permissionRepository.GetAllAsync(condition: permission => ids.Contains(permission.Id),
                                                           projection: permission => new Permission { Id = permission.Id, Name = permission.Name, PermissionGroup = permission.PermissionGroup },
                                                           cancellationToken: cancellationToken)];
    }

    private async Task<List<int>> GetRolePermissionIdsAsync(List<int> roleIds, CancellationToken cancellationToken)
    {
        if (roleIds.Count == 0)
            return [];

        var relations = await _rolePermissionRepository.GetAllAsync(condition: relation => roleIds.Contains(relation.RoleId),
                                                                    projection: relation => new RolePermissionRelation { PermissionId = relation.PermissionId },
                                                                    cancellationToken: cancellationToken);

        return [.. relations.Select(relation => relation.PermissionId).Distinct()];
    }

    private async Task<List<int>> GetUserRoleIdsAsync(int userId, CancellationToken cancellationToken)
    {
        var relations = await _userRoleRepository.GetAllAsync(condition: relation => relation.UserId == userId,
                                                              projection: relation => new UserRoleRelation { RoleId = relation.RoleId },
                                                              cancellationToken: cancellationToken);

        return [.. relations.Select(relation => relation.RoleId)];
    }

    private void Forbid() => Request.Response.ThrowWithForbidden();
}
