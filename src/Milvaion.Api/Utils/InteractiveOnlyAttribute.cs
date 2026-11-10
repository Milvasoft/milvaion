using Microsoft.AspNetCore.Mvc.Filters;
using Milvaion.Application.Utils.Extensions;

namespace Milvaion.Api.Utils;

/// <summary>
/// Rejects api key callers, for endpoints that must only be reachable from an interactive session.
/// </summary>
/// <remarks>
/// A filter rather than a scheme list on the attribute: the bearer scheme name differs between local and OIDC setups,
/// and on a filter that difference does not have to be known here.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class InteractiveOnlyAttribute : Attribute, IAuthorizationFilter
{
    /// <inheritdoc/>
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (context.HttpContext.User.HasClaim(claim => claim.Type == ApiKeyAuthenticationDefaults.ApiKeyIdClaimName))
            context.HttpContext.Response.ThrowWithForbidden();
    }
}
