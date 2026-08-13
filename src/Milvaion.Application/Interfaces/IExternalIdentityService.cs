using System.Security.Claims;

namespace Milvaion.Application.Interfaces;

/// <summary>
/// Bridges an externally authenticated identity (OIDC or LDAP) to Milvaion's local model. It keeps a
/// shadow user in step with the provider on each sign-in and resolves the permission claims that the
/// rest of the authorization stack expects, so identity is owned by the provider while permissions
/// stay owned here.
/// </summary>
public interface IExternalIdentityService
{
    /// <summary>
    /// Resolves (creating on first sight) the shadow user for <paramref name="descriptor"/>, syncs its
    /// role membership to the provider's roles, records the sign-in, and returns the claims to add to
    /// the request principal: the user name and one role claim per granted permission.
    /// </summary>
    Task<IReadOnlyList<Claim>> ResolveAndBuildClaimsAsync(ExternalIdentityDescriptor descriptor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the request's permission claims for the external identity, served from a short-lived Redis
    /// cache so most requests do no database work. On a cache miss it provisions/refreshes under a
    /// per-identity distributed lock, so the burst of parallel requests a browser fires on sign-in resolves
    /// to a single provision rather than many racing inserts, then caches and returns the result.
    /// </summary>
    Task<IReadOnlyList<Claim>> GetOrBuildClaimsAsync(ExternalIdentityDescriptor descriptor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates every cached external-identity claim set. Call this after a change that alters what a
    /// provider role grants (its permission set) or removes a role, so external users pick up the new
    /// authorization on their next request instead of waiting for the cache to expire.
    /// </summary>
    Task InvalidateAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The provider-neutral view of an externally authenticated user, built from an OIDC token or an LDAP
/// entry before it is mapped onto the local model.
/// </summary>
public sealed record ExternalIdentityDescriptor
{
    /// <summary>Which provider authenticated the user.</summary>
    public ExternalProvider Provider { get; init; }

    /// <summary>Issuer/authority that owns the identity (OIDC issuer URL, or the LDAP host).</summary>
    public string Issuer { get; init; }

    /// <summary>Stable per-user identifier (OIDC <c>sub</c>, or the LDAP object identifier).</summary>
    public string Subject { get; init; }

    /// <summary>Login name.</summary>
    public string UserName { get; init; }

    /// <summary>Email, when the provider supplies one.</summary>
    public string Email { get; init; }

    /// <summary>Given name.</summary>
    public string Name { get; init; }

    /// <summary>Family name.</summary>
    public string Surname { get; init; }

    /// <summary>Role/group names carried by the provider. Milvaion mirrors these as roles and their permissions are assigned here.</summary>
    public IReadOnlyCollection<string> RoleNames { get; init; } = [];
}
