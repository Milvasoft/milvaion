namespace Milvaion.Application.Interfaces;

/// <summary>
/// Drops cached authentication state for an api key.
/// </summary>
/// <remarks>
/// Api key lookups are cached so that authentication does not hit the database on every request. That cache has
/// to be invalidated the moment a key is revoked or its permissions change, otherwise the key keeps working for
/// the remainder of the cache lifetime - the exact failure a revoke button is supposed to prevent.
/// </remarks>
public interface IApiKeyCacheInvalidator
{
    /// <summary>
    /// Removes the cached entry for the given api key.
    /// </summary>
    Task InvalidateAsync(int apiKeyId, CancellationToken cancellationToken = default);
}
