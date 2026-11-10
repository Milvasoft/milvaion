using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace Milvaion.Application.Utils.PermissionManager;

/// <summary>
/// Keeps <see cref="PermissionCatalog.App.SuperAdmin"/> out of reach of anyone who does not already hold it.
/// </summary>
/// <remarks>
/// SuperAdmin is an ordinary permission row, so without this anyone allowed to edit roles, users or API keys could
/// grant it to themselves. Only changes count: a delegated admin saving a role that already has SuperAdmin is fine,
/// because the UI always resends the full list. A request without an HTTP context is an internal call.
/// </remarks>
public sealed class SuperAdminGrantGuard(IHttpContextAccessor httpContextAccessor,
                                         IMilvaionRepositoryBase<Permission> permissionRepository,
                                         IMilvaionRepositoryBase<RolePermissionRelation> rolePermissionRepository,
                                         IMilvaionRepositoryBase<MilvaionApiKeyPermissionRelation> apiKeyPermissionRepository,
                                         IMilvaionRepositoryBase<UserRoleRelation> userRoleRepository)
{
    private const string _unknownPermissionMessage = "One or more permissions do not exist.";

    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly IMilvaionRepositoryBase<Permission> _permissionRepository = permissionRepository;
    private readonly IMilvaionRepositoryBase<RolePermissionRelation> _rolePermissionRepository = rolePermissionRepository;
    private readonly IMilvaionRepositoryBase<MilvaionApiKeyPermissionRelation> _apiKeyPermissionRepository = apiKeyPermissionRepository;
    private readonly IMilvaionRepositoryBase<UserRoleRelation> _userRoleRepository = userRoleRepository;

    /// <summary>
    /// Validates the permission list of a role. Pass a null <paramref name="roleId"/> for a role being created.
    /// </summary>
    /// <returns>An error message when a permission does not exist, otherwise null.</returns>
    public async Task<string> ValidateRolePermissionsAsync(int? roleId, IEnumerable<int> requestedPermissionIds, CancellationToken cancellationToken)
    {
        var current = roleId is null
            ? []
            : await _rolePermissionRepository.GetAllAsync(condition: r => r.RoleId == roleId.Value,
                                                          projection: r => new RolePermissionRelation { PermissionId = r.PermissionId },
                                                          cancellationToken: cancellationToken);

        return await ValidatePermissionChangeAsync(requestedPermissionIds, current.Select(r => r.PermissionId), cancellationToken);
    }

    /// <summary>
    /// Validates the permission list of an API key. Pass a null <paramref name="apiKeyId"/> for a key being created.
    /// </summary>
    /// <returns>An error message when a permission does not exist, otherwise null.</returns>
    public async Task<string> ValidateApiKeyPermissionsAsync(int? apiKeyId, IEnumerable<int> requestedPermissionIds, CancellationToken cancellationToken)
    {
        var current = apiKeyId is null
            ? []
            : await _apiKeyPermissionRepository.GetAllAsync(condition: r => r.MilvaionApiKeyId == apiKeyId.Value,
                                                            projection: r => new MilvaionApiKeyPermissionRelation { PermissionId = r.PermissionId },
                                                            cancellationToken: cancellationToken);

        return await ValidatePermissionChangeAsync(requestedPermissionIds, current.Select(r => r.PermissionId), cancellationToken);
    }

    /// <summary>
    /// Requires SuperAdmin to add or remove a SuperAdmin role on a user. Pass a null <paramref name="userId"/> for a
    /// user being created.
    /// </summary>
    public async Task EnsureRoleChangeAllowedAsync(int? userId, IEnumerable<int> requestedRoleIds, CancellationToken cancellationToken)
    {
        if (CallerIsSuperAdmin())
            return;

        var requested = (requestedRoleIds ?? []).ToHashSet();
        var current = userId is null ? [] : await GetUserRoleIdsAsync(userId.Value, cancellationToken);

        requested.SymmetricExceptWith(current);

        if (await AnyGrantsSuperAdminAsync(requested, cancellationToken))
            ThrowForbidden();
    }

    /// <summary>
    /// Requires SuperAdmin to change the credentials or lockout of a user who holds SuperAdmin.
    /// </summary>
    public async Task EnsureCanManageUserAsync(int userId, CancellationToken cancellationToken)
    {
        if (CallerIsSuperAdmin())
            return;

        if (await AnyGrantsSuperAdminAsync(await GetUserRoleIdsAsync(userId, cancellationToken), cancellationToken))
            ThrowForbidden();
    }

    private async Task<string> ValidatePermissionChangeAsync(IEnumerable<int> requestedPermissionIds, IEnumerable<int> currentPermissionIds, CancellationToken cancellationToken)
    {
        var requested = (requestedPermissionIds ?? []).Distinct().ToList();

        if (requested.Count > 0)
        {
            var existing = await _permissionRepository.GetAllAsync(condition: p => requested.Contains(p.Id),
                                                                   projection: p => new Permission { Id = p.Id },
                                                                   cancellationToken: cancellationToken);

            if (existing.Count != requested.Count)
                return _unknownPermissionMessage;
        }

        if (CallerIsSuperAdmin())
            return null;

        var superAdminId = await GetSuperAdminPermissionIdAsync(cancellationToken);

        if (superAdminId.HasValue && requested.Contains(superAdminId.Value) != currentPermissionIds.Contains(superAdminId.Value))
            ThrowForbidden();

        return null;
    }

    private async Task<List<int>> GetUserRoleIdsAsync(int userId, CancellationToken cancellationToken)
    {
        var relations = await _userRoleRepository.GetAllAsync(condition: r => r.UserId == userId,
                                                              projection: r => new UserRoleRelation { RoleId = r.RoleId },
                                                              cancellationToken: cancellationToken);

        return [.. relations.Select(r => r.RoleId)];
    }

    private async Task<bool> AnyGrantsSuperAdminAsync(ICollection<int> roleIds, CancellationToken cancellationToken)
    {
        if (roleIds.Count == 0)
            return false;

        var superAdminId = await GetSuperAdminPermissionIdAsync(cancellationToken);

        if (!superAdminId.HasValue)
            return false;

        var relation = await _rolePermissionRepository.GetFirstOrDefaultAsync(r => r.PermissionId == superAdminId.Value && roleIds.Contains(r.RoleId),
                                                                              r => new RolePermissionRelation { Id = r.Id },
                                                                              cancellationToken: cancellationToken);

        return relation is not null;
    }

    private async Task<int?> GetSuperAdminPermissionIdAsync(CancellationToken cancellationToken)
    {
        var permission = await _permissionRepository.GetFirstOrDefaultAsync(p => p.PermissionGroup == nameof(PermissionCatalog.App) && p.Name == nameof(PermissionCatalog.App.SuperAdmin),
                                                                            p => new Permission { Id = p.Id },
                                                                            cancellationToken: cancellationToken);

        return permission?.Id;
    }

    private bool CallerIsSuperAdmin()
        => _httpContextAccessor.HttpContext is not { } httpContext
           || httpContext.User.FindAll(ClaimTypes.Role).Any(claim => claim.Value == PermissionCatalog.App.SuperAdmin);

    private void ThrowForbidden() => _httpContextAccessor.HttpContext.Response.ThrowWithForbidden();
}
