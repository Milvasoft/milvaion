using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.PermissionManager;
using Milvaion.Domain;
using Milvasoft.Caching.Redis.Accessor;
using System.Linq.Expressions;
using System.Security.Claims;

namespace Milvaion.Infrastructure.Services;

/// <summary>
/// Maps an externally authenticated identity onto the local model. Identity and role membership come
/// from the provider; the permission set of each role is owned in Milvaion. On sign-in this ensures
/// the roles exist, provisions the shadow user on first sight, records the login, and returns the
/// permission claims the authorization stack matches on.
/// </summary>
public class ExternalIdentityService(IMilvaionRepositoryBase<User> userRepository,
                                     IMilvaionRepositoryBase<Role> roleRepository,
                                     IRedisAccessor cache,
                                     StackExchange.Redis.IConnectionMultiplexer redis) : IExternalIdentityService
{
    private readonly IMilvaionRepositoryBase<User> _userRepository = userRepository;
    private readonly IMilvaionRepositoryBase<Role> _roleRepository = roleRepository;
    private readonly IRedisAccessor _cache = cache;
    private readonly StackExchange.Redis.IConnectionMultiplexer _redis = redis;

    /// <summary>Skip the LastLoginDate write when it was refreshed this recently, to keep hot-path writes rare.</summary>
    private static readonly TimeSpan _lastLoginThrottle = TimeSpan.FromMinutes(10);

    /// <summary>How long a resolved claim set is served from Redis before the next sign-in re-resolves it.</summary>
    private static readonly TimeSpan _claimsCacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>Lifetime of the per-identity provisioning lock: long enough to cover a first-sight insert, short enough to self-heal if the holder dies.</summary>
    private static readonly TimeSpan _provisionLockTtl = TimeSpan.FromSeconds(10);

    /// <summary>Pause between attempts to take the provisioning lock while another request holds it.</summary>
    private static readonly TimeSpan _provisionLockRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>Give up taking the lock after this many attempts (~5s) and resolve directly; the unique indexes stay the final guard.</summary>
    private const int _provisionLockMaxAttempts = 50;

    /// <summary>Redis key holding the claim-cache version. Bumping it invalidates every cached identity at once.</summary>
    private const string _claimsCacheVersionKey = "oidc-identity:version";

    /// <summary>Version key lifetime. Long-lived so it survives idle periods; only ever overwritten by an invalidation.</summary>
    private static readonly TimeSpan _claimsCacheVersionTtl = TimeSpan.FromDays(30);

    /// <summary>How long the version is trusted from process memory before re-reading Redis, so the hot path stays free of an extra round-trip while an invalidation still propagates within seconds.</summary>
    private static readonly TimeSpan _versionMemoTtl = TimeSpan.FromSeconds(10);

    // Process-wide memo of the cache version. Benign races only cost an extra Redis read; correctness comes from
    // the value in Redis, which every instance converges on within _versionMemoTtl.
    private static int _versionMemo;
    private static DateTime _versionMemoExpiresUtc;

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Claim>> GetOrBuildClaimsAsync(ExternalIdentityDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        // The role set the provider asserted is folded into the cached payload, so a membership change on the
        // provider (new token, same subject) is detected and re-resolved even without an explicit invalidation.
        var rolesHash = ComputeRolesHash([.. descriptor.RoleNames ?? [], .. descriptor.LocalRoleNames ?? []]);
        var cacheKey = await BuildCacheKeyAsync(descriptor);

        var cached = await _cache.GetAsync<CachedIdentity>(cacheKey);

        if (IsFresh(cached, rolesHash))
            return ToClaims(cached.Claims);

        var database = _redis.GetDatabase();
        var lockKey = $"lock:{cacheKey}";
        var lockToken = Guid.NewGuid().ToString("N");
        var lockAcquired = false;

        try
        {
            // A browser fires several requests the instant it holds the token. Serialize them per identity so
            // exactly one provisions and writes the cache; the rest wait briefly and read that result, instead
            // of every request racing to insert the same user and roles.
            for (var attempt = 0; attempt < _provisionLockMaxAttempts && !lockAcquired; attempt++)
            {
                lockAcquired = await database.LockTakeAsync(lockKey, lockToken, _provisionLockTtl);

                if (lockAcquired)
                    break;

                var raced = await _cache.GetAsync<CachedIdentity>(cacheKey);
                if (IsFresh(raced, rolesHash))
                    return ToClaims(raced.Claims);

                await Task.Delay(_provisionLockRetryDelay, cancellationToken);
            }

            // Re-check under the lock: the winner may have finished between our miss and acquiring it.
            var afterLock = await _cache.GetAsync<CachedIdentity>(cacheKey);

            if (IsFresh(afterLock, rolesHash))
                return ToClaims(afterLock.Claims);

            var claims = await ResolveAndBuildClaimsAsync(descriptor, cancellationToken);

            var payload = new CachedIdentity(rolesHash, [.. claims.Select(c => new CachedClaim(c.Type, c.Value))]);

            await _cache.SetAsync(cacheKey, payload, _claimsCacheTtl);

            return claims;
        }
        finally
        {
            if (lockAcquired)
                await database.LockReleaseAsync(lockKey, lockToken);
        }
    }

    /// <inheritdoc/>
    public async Task InvalidateAllAsync(CancellationToken cancellationToken = default)
    {
        var version = await GetCurrentVersionAsync() + 1;

        // Stored as a string: the cache accessor's GetAsync<T> is reference-typed, so a plain int can't round-trip.
        await _cache.SetAsync(_claimsCacheVersionKey, version.ToString(), _claimsCacheVersionTtl);

        // Make the bump effective immediately on this instance; others pick it up when their memo expires.
        _versionMemo = version;
        _versionMemoExpiresUtc = DateTime.UtcNow.Add(_versionMemoTtl);
    }

    /// <summary>Builds the version-scoped cache key. A version bump changes the prefix so every prior entry is unreachable and expires on its own TTL.</summary>
    private async Task<string> BuildCacheKeyAsync(ExternalIdentityDescriptor descriptor)
    {
        if (DateTime.UtcNow >= _versionMemoExpiresUtc)
        {
            _versionMemo = await GetCurrentVersionAsync();
            _versionMemoExpiresUtc = DateTime.UtcNow.Add(_versionMemoTtl);
        }

        return $"oidc-identity:{_versionMemo}:{descriptor.Issuer}:{descriptor.Subject}";
    }

    /// <summary>Reads the current claim-cache version from Redis, defaulting to 0 when it has never been set.</summary>
    private async Task<int> GetCurrentVersionAsync()
    {
        var raw = await _cache.GetAsync<string>(_claimsCacheVersionKey);

        return int.TryParse(raw, out var version) ? version : 0;
    }

    /// <summary>A cached entry is usable only if it carries claims and was built for the same provider role set.</summary>
    private static bool IsFresh(CachedIdentity cached, string rolesHash)
        => cached is { Claims.Count: > 0 } && string.Equals(cached.RolesHash, rolesHash, StringComparison.Ordinal);

    /// <summary>Order-insensitive fingerprint of the provider's role names, used to detect membership changes.</summary>
    private static string ComputeRolesHash(IReadOnlyCollection<string> roleNames)
        => string.Join(",", (roleNames ?? []).Where(n => !string.IsNullOrWhiteSpace(n))
                                             .Select(n => n.Trim())
                                             .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Claim>> ResolveAndBuildClaimsAsync(ExternalIdentityDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        var roles = await EnsureRolesAsync(descriptor.RoleNames, descriptor.Provider, cancellationToken);

        roles.AddRange(await GetMappedLocalRolesAsync(descriptor.LocalRoleNames, cancellationToken));

        var user = await _userRepository.GetFirstOrDefaultAsync(u => u.Issuer == descriptor.Issuer && u.ExternalSubject == descriptor.Subject,
                                                                projection: _externalLookup,
                                                                cancellationToken: cancellationToken);

        if (user is null)
        {
            user = new User
            {
                Provider = descriptor.Provider,
                Issuer = descriptor.Issuer,
                ExternalSubject = descriptor.Subject,
                UserName = descriptor.UserName,
                NormalizedUserName = descriptor.UserName?.ToUpperInvariant(),
                Email = descriptor.Email,
                NormalizedEmail = descriptor.Email?.ToUpperInvariant(),
                EmailConfirmed = true,
                Name = descriptor.Name,
                Surname = descriptor.Surname,
                LastLoginDate = DateTime.UtcNow,
                RoleRelations = [.. roles.Select(r => new UserRoleRelation { RoleId = r.Id })]
            };

            try
            {
                await _userRepository.AddAsync(user, cancellationToken);
            }
            catch
            {
                // A concurrent first sign-in for the same identity may have created it (the burst of parallel
                // requests a browser fires). The unique index rejects the duplicate; re-read instead of failing.
                var created = await _userRepository.GetFirstOrDefaultAsync(u => u.Issuer == descriptor.Issuer && u.ExternalSubject == descriptor.Subject,
                                                                          projection: _externalLookup,
                                                                          cancellationToken: cancellationToken);

                if (created is null)
                    throw;

                user = created;
            }
        }
        else
        {
            await SyncIdentityAsync(user, descriptor, cancellationToken);

            if (user.LastLoginDate is null || DateTime.UtcNow - user.LastLoginDate.Value > _lastLoginThrottle)
            {
                user.LastLoginDate = DateTime.UtcNow;

                await _userRepository.UpdateAsync(user, cancellationToken, u => u.LastLoginDate);
            }
        }

        // Authorization follows the roles the provider asserted this sign-in, resolved against the
        // permissions an admin assigned to those roles in Milvaion.
        var permissionClaims = roles.Where(r => r.RolePermissionRelations != null)
                                    .SelectMany(r => r.RolePermissionRelations.Select(rp => rp.Permission))
                                    .Where(p => p != null)
                                    .Select(p => p.FormatPermissionAndGroup())
                                    .Distinct()
                                    .Select(permission => new Claim(ClaimTypes.Role, permission));

        var claims = new List<Claim> { new(ClaimTypes.Name, user.UserName) };
        claims.AddRange(permissionClaims);

        return claims;
    }

    /// <summary>
    /// Refreshes the shadow record from the provider, which owns the identity. A sign-in that arrived without a user name
    /// claim stored the subject as the name, so a later token carrying the real one has to replace it.
    /// </summary>
    private async Task SyncIdentityAsync(User user, ExternalIdentityDescriptor descriptor, CancellationToken cancellationToken)
    {
        var email = string.IsNullOrWhiteSpace(descriptor.Email) ? user.Email : descriptor.Email;
        var name = string.IsNullOrWhiteSpace(descriptor.Name) ? user.Name : descriptor.Name;
        var userName = user.UserName;

        if (!string.IsNullOrWhiteSpace(descriptor.UserName) && descriptor.UserName != descriptor.Subject && descriptor.UserName != user.UserName)
        {
            var taken = await _userRepository.GetFirstOrDefaultAsync(u => u.UserName == descriptor.UserName && u.Id != user.Id,
                                                                     projection: u => new User { Id = u.Id },
                                                                     cancellationToken: cancellationToken);

            if (taken is null)
                userName = descriptor.UserName;
        }

        if (userName == user.UserName && email == user.Email && name == user.Name)
            return;

        user.UserName = userName;
        user.NormalizedUserName = userName?.ToUpperInvariant();
        user.Email = email;
        user.NormalizedEmail = email?.ToUpperInvariant();
        user.Name = name;

        await _userRepository.UpdateAsync(user, cancellationToken, u => u.UserName, u => u.NormalizedUserName, u => u.Email, u => u.NormalizedEmail, u => u.Name);
    }

    /// <summary>
    /// Loads the local roles an explicit mapping grants. Provider-owned roles with the same name do not qualify, and a
    /// name that matches nothing grants nothing.
    /// </summary>
    private async Task<List<Role>> GetMappedLocalRolesAsync(IReadOnlyCollection<string> roleNames, CancellationToken cancellationToken)
    {
        var wanted = (roleNames ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();

        if (wanted.Count == 0)
            return [];

        return [.. await _roleRepository.GetAllAsync(condition: r => wanted.Contains(r.Name) && r.Provider == ExternalProvider.Local,
                                                     projection: _roleWithPermissions,
                                                     cancellationToken: cancellationToken)];
    }

    /// <summary>
    /// Loads the provider's roles with their permissions, creating any that do not exist yet (with an
    /// empty permission set for an admin to fill in). Only roles owned by this provider can match: a group
    /// named after a local role, or after one mirrored from another provider, is dropped instead of inheriting it.
    /// </summary>
    private async Task<List<Role>> EnsureRolesAsync(IReadOnlyCollection<string> roleNames, ExternalProvider provider, CancellationToken cancellationToken)
    {
        var wanted = (roleNames ?? []).Where(n => !string.IsNullOrWhiteSpace(n))
                                      .Where(n => !string.Equals(n, nameof(PermissionCatalog.App.SuperAdmin), StringComparison.OrdinalIgnoreCase))
                                      .Distinct()
                                      .ToList();

        if (wanted.Count == 0)
            return [];

        var matches = (await _roleRepository.GetAllAsync(condition: r => wanted.Contains(r.Name),
                                                         projection: _roleWithPermissions,
                                                         cancellationToken: cancellationToken)).ToList();

        var existing = matches.Where(r => r.Provider == provider).ToList();

        foreach (var name in wanted.Except(matches.Select(r => r.Name)))
        {
            var role = new Role { Name = name, Provider = provider, RolePermissionRelations = [] };

            try
            {
                await _roleRepository.AddAsync(role, cancellationToken);

                existing.Add(role);
            }
            catch
            {
                // Two different users signing in at once can both try to create the same provider role.
                // The unique index on the name rejects the duplicate; re-read the winner instead of failing.
                var created = await _roleRepository.GetFirstOrDefaultAsync(r => r.Name == name && r.Provider == provider,
                                                                           projection: _roleWithPermissions,
                                                                           cancellationToken: cancellationToken);

                // Null when the unique name belongs to a local role or another provider. That group simply grants nothing.
                if (created is null)
                    continue;

                if (created.Provider == provider)
                    existing.Add(created);
            }
        }

        return existing;
    }

    private static readonly Expression<Func<User, User>> _externalLookup = u => new User
    {
        Id = u.Id,
        UserName = u.UserName,
        Email = u.Email,
        Name = u.Name,
        LastLoginDate = u.LastLoginDate
    };

    private static readonly Expression<Func<Role, Role>> _roleWithPermissions = r => new Role
    {
        Id = r.Id,
        Name = r.Name,
        Provider = r.Provider,
        RolePermissionRelations = r.RolePermissionRelations.Select(rp => new RolePermissionRelation
        {
            Id = rp.Id,
            RoleId = rp.RoleId,
            PermissionId = rp.PermissionId,
            Permission = new Permission { Id = rp.Permission.Id, Name = rp.Permission.Name, PermissionGroup = rp.Permission.PermissionGroup }
        }).ToList()
    };

    private static List<Claim> ToClaims(IEnumerable<CachedClaim> cached) => [.. cached.Select(c => new Claim(c.Type, c.Value))];

    /// <summary>Cached identity payload: the provider role set it was built for, plus the resolved permission claims.</summary>
    private sealed record CachedIdentity(string RolesHash, List<CachedClaim> Claims);

    /// <summary>Compact, serializable projection of a <see cref="Claim"/> for the Redis claim cache.</summary>
    private sealed record CachedClaim(string Type, string Value);
}
