namespace Milvaion.Application.Interfaces;

/// <summary>
/// The rules that stop a caller from handing out more than it holds. Every method fails the request with 403.
/// </summary>
/// <remarks>
/// A caller may only grant permissions it holds itself, and only a super admin may touch anything that carries the
/// super admin permission. Permissions a role or key already has are not "granted" by keeping them, so a caller can
/// still edit an existing record it could not have created. Code running outside a request (seeding, background work)
/// is not subject to these rules.
/// </remarks>
public interface IPrivilegeGuard
{
    /// <summary>Returns whether every one of these permission ids exists.</summary>
    Task<bool> AllPermissionsExistAsync(IEnumerable<int> permissionIds, CancellationToken cancellationToken = default);

    /// <summary>Checks that the caller may put these permissions on a new role.</summary>
    Task EnsureCanGrantPermissionsAsync(IEnumerable<int> permissionIds, CancellationToken cancellationToken = default);

    /// <summary>Checks that the caller may set this permission list on an existing role.</summary>
    Task EnsureCanChangeRolePermissionsAsync(int roleId, IEnumerable<int> permissionIds, CancellationToken cancellationToken = default);

    /// <summary>Checks that the caller may modify or delete this role.</summary>
    Task EnsureCanManageRoleAsync(int roleId, CancellationToken cancellationToken = default);

    /// <summary>Checks that the caller may put these roles on a new user.</summary>
    Task EnsureCanGrantRolesAsync(IEnumerable<int> roleIds, CancellationToken cancellationToken = default);

    /// <summary>Checks that the caller may set this role list on an existing user.</summary>
    Task EnsureCanChangeUserRolesAsync(int userId, IEnumerable<int> roleIds, CancellationToken cancellationToken = default);

    /// <summary>Checks that the caller may modify or delete this user.</summary>
    Task EnsureCanManageUserAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks that the caller may put these permissions on an api key, new when <paramref name="apiKeyId"/> is null.
    /// The super admin permission can never be put on a key, whoever asks.
    /// </summary>
    Task EnsureCanGrantApiKeyPermissionsAsync(int? apiKeyId, IEnumerable<int> permissionIds, CancellationToken cancellationToken = default);
}
