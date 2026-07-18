using ModelContextProtocol;
using System.Security.Claims;

namespace Milvaion.Api.Mcp;

/// <summary>
/// Enforces Milvaion permissions inside MCP tools.
/// </summary>
/// <remarks>
/// MCP tools are not MVC actions, so <c>[Auth(PermissionCatalog...)]</c> never runs for them. Authentication is
/// still handled by the endpoint - <c>/mcp</c> requires an authenticated caller - but the per-permission check
/// has to happen here, or every caller with any valid credential would reach every tool.
/// <para>
/// Permissions arrive as role claims, put there by the login token or by
/// <c>ApiKeyAuthenticationHandler</c>, so one check covers both interactive users and api keys.
/// </para>
/// </remarks>
public class McpPermissionGuard(IHttpContextAccessor httpContextAccessor)
{
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;

    /// <summary>
    /// Throws when the caller does not hold <paramref name="permission"/> or the super admin permission.
    /// </summary>
    /// <remarks>
    /// The thrown <see cref="McpException"/> is what the calling model sees. The message names the missing
    /// permission on purpose: an agent that is told exactly what it lacks stops retrying, and the user can go
    /// and grant it.
    /// </remarks>
    public void Require(string permission)
    {
        var user = _httpContextAccessor.HttpContext?.User;

        if (user?.Identity?.IsAuthenticated != true)
            throw new McpException("Not authenticated. Send a Milvaion api key in the X-ApiKey header.");

        if (user.IsInRole(PermissionCatalog.App.SuperAdmin) || user.IsInRole(permission))
            return;

        throw new McpException($"This credential does not have the '{permission}' permission. " +
                               "Grant it to the api key in Milvaion under User Management > Api Keys, or use a different key.");
    }

    /// <summary>
    /// Returns true when the caller holds the permission, without throwing.
    /// Useful for trimming optional detail out of a response rather than failing the whole call.
    /// </summary>
    public bool Has(string permission)
    {
        var user = _httpContextAccessor.HttpContext?.User;

        if (user?.Identity?.IsAuthenticated != true)
            return false;

        return user.IsInRole(PermissionCatalog.App.SuperAdmin) || user.IsInRole(permission);
    }

    /// <summary>
    /// Name of the current caller, for logging and for stamping trigger reasons.
    /// </summary>
    public string CallerName => _httpContextAccessor.HttpContext?.User?.FindFirstValue(ClaimTypes.Name) ?? "unknown";
}
