using Microsoft.AspNetCore.Authentication;

namespace Milvaion.Api.Utils;

/// <summary>
/// Constants for the api key authentication scheme.
/// </summary>
public static class ApiKeyAuthenticationDefaults
{
    /// <summary>
    /// Name of the api key authentication scheme.
    /// </summary>
    public const string AuthenticationScheme = "ApiKey";

    /// <summary>
    /// Claim added to every api key principal so endpoints can tell a machine caller from an interactive user.
    /// </summary>
    public const string ApiKeyIdClaimName = "akid";
}

/// <summary>
/// Options for the api key authentication scheme.
/// </summary>
public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>
    /// How long a validated api key record stays cached before it is read from the database again.
    /// Revocation invalidates the cache explicitly, so this is only a backstop.
    /// </summary>
    public TimeSpan CacheLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Minimum interval between <c>LastUsedAt</c> writes for a single key.
    /// Without this a busy integration would cause a database write on every request for a column nobody reads
    /// at that resolution.
    /// </summary>
    public TimeSpan LastUsedWriteInterval { get; set; } = TimeSpan.FromMinutes(5);
}
