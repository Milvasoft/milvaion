using Microsoft.AspNetCore.Authentication;
using Milvaion.Application.Utils.Projects;
using System.Security.Claims;

namespace Milvaion.Api.Services.Projects;

/// <summary>
/// Puts the caller's permissions in the request's project into the principal, so <c>[Auth]</c> and the MCP permission
/// guard keep working unchanged. Runs after every successful authentication, including the ones the authorization
/// policy performs per scheme, so it is idempotent: it always starts from the principal the handler produced.
/// </summary>
public sealed class ProjectClaimsTransformation(IHttpContextAccessor httpContextAccessor, ProjectRequestResolver resolver) : IClaimsTransformation
{
    private const string _outcomeCacheKey = "Milvaion.ProjectOutcomes";

    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly ProjectRequestResolver _resolver = resolver;

    /// <inheritdoc/>
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var httpContext = _httpContextAccessor.HttpContext;

        if (httpContext is null || principal?.Identity?.IsAuthenticated != true)
            return principal;

        var outcome = await GetOutcomeAsync(httpContext, principal);

        httpContext.Items[ProjectResolution.ItemKey] = outcome.Resolution;

        // A failed resolution keeps instance permissions only, so even an endpoint that is reached anyway grants nothing in a project.
        if (!outcome.Resolution.Succeeded)
            return Rebuild(principal, keepProjectPermissions: false, []);

        if (outcome.KeepTokenProjectPermissions && outcome.AddedPermissions.Count == 0)
            return principal;

        return Rebuild(principal, outcome.KeepTokenProjectPermissions, outcome.AddedPermissions);
    }

    private async Task<ProjectRequestOutcome> GetOutcomeAsync(HttpContext httpContext, ClaimsPrincipal principal)
    {
        // Several schemes can authenticate one request and each runs this transformation; resolve once per caller.
        if (httpContext.Items[_outcomeCacheKey] is not Dictionary<string, ProjectRequestOutcome> cache)
            httpContext.Items[_outcomeCacheKey] = cache = new Dictionary<string, ProjectRequestOutcome>(StringComparer.Ordinal);

        var cacheKey = principal.Identity?.AuthenticationType + "|" + principal.Identity?.Name;

        if (!cache.TryGetValue(cacheKey, out var outcome))
        {
            outcome = await _resolver.ResolveAsync(httpContext, principal, httpContext.RequestAborted);
            cache[cacheKey] = outcome;
        }

        return outcome;
    }

    /// <summary>
    /// Copies <paramref name="principal"/>, dropping project permissions unless <paramref name="keepProjectPermissions"/>
    /// and adding <paramref name="addedPermissions"/>.
    /// </summary>
    public static ClaimsPrincipal Rebuild(ClaimsPrincipal principal, bool keepProjectPermissions, IReadOnlyList<string> addedPermissions)
    {
        var identities = new List<ClaimsIdentity>();

        foreach (var identity in principal.Identities)
        {
            var claims = identity.Claims.Where(c => keepProjectPermissions || c.Type != identity.RoleClaimType || !PermissionScopes.IsProjectPermission(c.Value));

            identities.Add(new ClaimsIdentity(claims, identity.AuthenticationType, identity.NameClaimType, identity.RoleClaimType));
        }

        if (identities.Count > 0 && addedPermissions.Count > 0)
        {
            var first = identities[0];

            foreach (var permission in addedPermissions.Distinct(StringComparer.Ordinal))
            {
                if (!first.HasClaim(first.RoleClaimType, permission))
                    first.AddClaim(new Claim(first.RoleClaimType, permission));
            }
        }

        return new ClaimsPrincipal(identities);
    }
}
