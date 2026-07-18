using Microsoft.AspNetCore.Authorization;

namespace Milvaion.Api.Utils;

/// <summary>
/// Restricts an endpoint to api key callers, optionally requiring specific permissions.
/// </summary>
/// <remarks>
/// Use this instead of <c>[Auth]</c> when an endpoint is meant for machine callers only - a CI integration or an
/// MCP client - and should not be reachable with an interactive browser session.
/// <para>
/// For endpoints that should accept both a logged-in user and an api key, use <c>[Auth(...)]</c>: the default
/// authorization policy already names both authentication schemes, so api key callers satisfy it.
/// </para>
/// <para>
/// The header parsing, signature check, revocation and expiry checks all live in
/// <see cref="ApiKeyAuthenticationHandler"/>. This attribute only selects that scheme and applies the permission
/// requirement, so authorization stays in one place.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class ApiAuthAttribute : AuthorizeAttribute
{
    /// <summary>
    /// Requires a valid api key, with no particular permission.
    /// </summary>
    public ApiAuthAttribute()
    {
        AuthenticationSchemes = ApiKeyAuthenticationDefaults.AuthenticationScheme;
    }

    /// <summary>
    /// Requires a valid api key holding one of <paramref name="permissions"/>, or the super admin permission.
    /// </summary>
    public ApiAuthAttribute(params string[] permissions) : this()
    {
        Roles = string.Join(",", permissions.Append(PermissionCatalog.App.SuperAdmin));
    }
}
