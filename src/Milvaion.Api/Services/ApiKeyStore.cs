using StackExchange.Redis;
using System.Linq.Expressions;
using System.Text.Json;

namespace Milvaion.Api.Services;

/// <summary>
/// Authentication state of an api key, as cached.
/// </summary>
/// <param name="Id">Api key record id.</param>
/// <param name="Name">Human readable name, used for the principal identity.</param>
/// <param name="KeyVersion">Signing secret version the key was issued with.</param>
/// <param name="ExpiresAt">UTC expiry, if any.</param>
/// <param name="IsRevoked">Whether the key has been revoked.</param>
/// <param name="Permissions">Permission names granted to the key.</param>
public record CachedApiKey(int Id, string Name, int KeyVersion, DateTime? ExpiresAt, bool IsRevoked, List<string> Permissions);

/// <summary>
/// Reads api key records for authentication, cached in Redis.
/// </summary>
/// <remarks>
/// Redis rather than an in-process cache on purpose: with more than one API replica an in-process cache would
/// leave a revoked key working on every replica that did not handle the revoke request.
/// </remarks>
public class ApiKeyStore(IConnectionMultiplexer redis,
                         IMilvaionRepositoryBase<MilvaionApiKey> apiKeyRepository,
                         ILogger<ApiKeyStore> logger) : IApiKeyCacheInvalidator
{
    private readonly IConnectionMultiplexer _redis = redis;
    private readonly IMilvaionRepositoryBase<MilvaionApiKey> _apiKeyRepository = apiKeyRepository;
    private readonly ILogger<ApiKeyStore> _logger = logger;

    private const string _cacheKeyPrefix = "milvaion:apikey:";
    private const string _lastUsedKeyPrefix = "milvaion:apikey:lastused:";

    private static string CacheKey(int apiKeyId) => $"{_cacheKeyPrefix}{apiKeyId}";

    /// <summary>
    /// Projection used when loading an api key for authentication.
    /// </summary>
    /// <remarks>
    /// Permissions are emitted as <c>{PermissionGroup}.{Name}</c>, matching
    /// <see cref="Permission.FormatPermissionAndGroup"/> and what <c>AccountManager</c> puts in the login token.
    /// <c>Permission.Name</c> on its own is only the field name - "List", "SuperAdmin" - which would never match
    /// the values in <c>PermissionCatalog</c> and would fail every authorization check.
    /// The concatenation is inline rather than a call to <c>FormatPermissionAndGroup</c> because this expression
    /// has to be translatable to SQL.
    /// </remarks>
    private static readonly Expression<Func<MilvaionApiKey, CachedApiKey>> _projection = a => new CachedApiKey(a.Id,
                                                                                                              a.Name,
                                                                                                              a.KeyVersion,
                                                                                                              a.ExpiresAt,
                                                                                                              a.RevokedAt != null,
                                                                                                              a.ApiKeyPermissionRelations.Select(p => p.Permission.PermissionGroup + "." + p.Permission.Name).ToList());

    /// <summary>
    /// Gets the api key record, from cache when possible.
    /// </summary>
    public async Task<CachedApiKey> GetAsync(int apiKeyId, TimeSpan cacheLifetime, CancellationToken cancellationToken = default)
    {
        var db = TryGetDatabase();

        if (db != null)
        {
            try
            {
                var cached = await db.StringGetAsync(CacheKey(apiKeyId));

                if (cached.HasValue)
                    return JsonSerializer.Deserialize<CachedApiKey>((string)cached);
            }
            catch (Exception ex)
            {
                // A cache read failure must not lock everybody out - fall through to the database.
                _logger.LogWarning(ex, "Api key cache read failed for key {ApiKeyId}. Falling back to database.", apiKeyId);
            }
        }

        var apiKey = await _apiKeyRepository.GetByIdAsync(apiKeyId, projection: _projection, cancellationToken: cancellationToken);

        if (apiKey == null)
            return null;

        if (db != null)
        {
            try
            {
                await db.StringSetAsync(CacheKey(apiKeyId), JsonSerializer.Serialize(apiKey), cacheLifetime);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Api key cache write failed for key {ApiKeyId}.", apiKeyId);
            }
        }

        return apiKey;
    }

    /// <inheritdoc/>
    public async Task InvalidateAsync(int apiKeyId, CancellationToken cancellationToken = default)
    {
        var db = TryGetDatabase();

        if (db == null)
            return;

        try
        {
            await db.KeyDeleteAsync(CacheKey(apiKeyId));
        }
        catch (Exception ex)
        {
            // Worth shouting about: the key keeps working until the cache entry expires.
            _logger.LogError(ex, "Api key cache invalidation failed for key {ApiKeyId}. The key may remain usable until its cache entry expires.", apiKeyId);
        }
    }

    /// <summary>
    /// Returns true when a <c>LastUsedAt</c> write is due for this key, and claims the write window.
    /// </summary>
    public async Task<bool> ShouldWriteLastUsedAsync(int apiKeyId, TimeSpan interval)
    {
        var db = TryGetDatabase();

        // Without Redis, skip the throttle rather than writing on every request.
        if (db == null)
            return false;

        try
        {
            // Set-if-not-exists doubles as the throttle: whoever sets it owns this interval.
            return await db.StringSetAsync($"{_lastUsedKeyPrefix}{apiKeyId}", DateTime.UtcNow.ToString("O"), interval, When.NotExists);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Api key last-used throttle check failed for key {ApiKeyId}.", apiKeyId);
            return false;
        }
    }

    private IDatabase TryGetDatabase()
    {
        try
        {
            return _redis.IsConnected ? _redis.GetDatabase() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis unavailable for api key caching.");
            return null;
        }
    }
}
