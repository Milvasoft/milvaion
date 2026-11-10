using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces.Redis;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Utils;
using StackExchange.Redis;

namespace Milvaion.Infrastructure.Services.Redis;

/// <summary>
/// Redis-based distributed lock implementation.
/// </summary>
public class RedisLockService : IRedisLockService
{
    private readonly RedisConnectionService _redisConnection;
    private readonly RedisOptions _options;
    private readonly IMilvaLogger _logger;
    private readonly IDatabase _database;
    private readonly IRedisCircuitBreaker _circuitBreaker;

    // Use Lua script to atomically check owner and delete. This prevents releasing a lock that was acquired by another worker
    private const string _checkOwnerAndDeleteScript = @"
                if redis.call('get', KEYS[1]) == ARGV[1] then
                    return redis.call('del', KEYS[1])
                else
                    return 0
                end";

    // Use Lua script to atomically check owner and extend TTL
    private const string _checkOwnerAndExtendTTLScript = @"
                if redis.call('get', KEYS[1]) == ARGV[1] then
                    return redis.call('expire', KEYS[1], ARGV[2])
                else
                    return 0
                end";

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisLockService"/> class.
    /// </summary>
    public RedisLockService(RedisConnectionService redisConnection,
                            IOptions<RedisOptions> options,
                            IRedisCircuitBreaker circuitBreaker,
                            ILoggerFactory loggerFactory)
    {
        _redisConnection = redisConnection;
        _options = options.Value;
        _circuitBreaker = circuitBreaker;
        _logger = loggerFactory.CreateMilvaLogger<RedisLockService>();
        _database = _redisConnection.Database;
    }

    /// <inheritdoc/>
    public Task<bool> TryAcquireLockAsync(Guid jobId, string workerId, TimeSpan ttl, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    var lockKey = _options.GetLockKey(jobId);

                    // SET key value NX EX ttl
                    var acquired = await _database.StringSetAsync(lockKey, workerId, ttl, when: When.NotExists);

                    if (acquired)
                    {
                        _logger.Information("Lock acquired for job {JobId} by worker {WorkerId} (TTL: {Ttl}s)", jobId, workerId, (int)ttl.TotalSeconds);
                    }
                    else
                    {
                        var currentOwner = await _database.StringGetAsync(lockKey);
                        _logger.Warning("Failed to acquire lock for job {JobId}. Already locked by {CurrentOwner}", jobId, currentOwner);
                    }

                    return acquired;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error acquiring lock for job {JobId}", jobId);
                    throw;
                }
            },
            fallback: async () => false,
            operationName: "TryAcquireLock",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<bool> ReleaseLockAsync(Guid jobId, string workerId, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    var lockKey = _options.GetLockKey(jobId);

                    var result = await _database.ScriptEvaluateAsync(_checkOwnerAndDeleteScript, [lockKey], [workerId]);

                    var released = (int)result == 1;

                    if (released)
                    {
                        _logger.Debug("Lock released for job {JobId} by worker {WorkerId}", jobId, workerId);
                    }
                    else
                    {
                        _logger.Warning("Failed to release lock for job {JobId}. Worker {WorkerId} does not own the lock", jobId, workerId);
                    }

                    return released;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error releasing lock for job {JobId}", jobId);
                    throw;
                }
            },
            fallback: async () => false,
            operationName: "ReleaseLock",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<bool> IsLockedAsync(Guid jobId, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    var lockKey = _options.GetLockKey(jobId);

                    return await _database.KeyExistsAsync(lockKey);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error checking lock status for job {JobId}", jobId);
                    throw;
                }
            },
            fallback: async () => false,
            operationName: "IsLocked",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<string> GetLockOwnerAsync(Guid jobId, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    var lockKey = _options.GetLockKey(jobId);

                    var owner = await _database.StringGetAsync(lockKey);

                    return owner.HasValue ? owner.ToString() : null;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error getting lock owner for job {JobId}", jobId);
                    throw;
                }
            },
            fallback: async () => null,
            operationName: "GetLockOwner",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<bool> ExtendLockAsync(Guid jobId, string workerId, TimeSpan ttl, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    var lockKey = _options.GetLockKey(jobId);

                    var result = await _database.ScriptEvaluateAsync(_checkOwnerAndExtendTTLScript, [lockKey], [workerId, (int)ttl.TotalSeconds]);

                    var extended = (int)result == 1;

                    if (extended)
                    {
                        _logger.Debug("Lock extended for job {JobId} by worker {WorkerId} (TTL: {Ttl}s)", jobId, workerId, (int)ttl.TotalSeconds);
                    }

                    return extended;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error extending lock for job {JobId}", jobId);
                    throw;
                }
            },
            fallback: async () => false,
            operationName: "ExtendLock",
            cancellationToken: cancellationToken
        );

    /// <summary>
    /// Tries to acquire locks for multiple jobs atomically using Lua script.
    /// </summary>
    public Task<Dictionary<Guid, bool>> TryAcquireLocksBulkAsync(List<Guid> jobIds, string workerId, TimeSpan ttl, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    const string bulkLockScript = @"
                        local results = {}
                        local ttl = tonumber(ARGV[1])
                        local owner = ARGV[2]
                        for i, key in ipairs(KEYS) do
                            local acquired = redis.call('SET', key, owner, 'NX', 'EX', ttl)
                            if acquired then
                                table.insert(results, 1)
                            else
                                table.insert(results, 0)
                            end
                        end
                        return results";

                    var keys = jobIds.Select(id => (RedisKey)_options.GetLockKey(id)).ToArray();
                    var args = new RedisValue[] { (int)ttl.TotalSeconds, workerId };
                    var result = await _database.ScriptEvaluateAsync(bulkLockScript, keys, args);
                    var resultArray = (RedisResult[])result;
                    var lockResults = new Dictionary<Guid, bool>();

                    for (int i = 0; i < jobIds.Count; i++)
                    {
                        lockResults[jobIds[i]] = (int)resultArray[i] == 1;
                    }

                    var acquiredCount = lockResults.Count(kvp => kvp.Value);
                    _logger.Debug("Bulk lock acquisition: {Acquired}/{Total} locks acquired by {WorkerId}", acquiredCount, jobIds.Count, workerId);

                    return lockResults;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error during bulk lock acquisition for {Count} jobs", jobIds.Count);
                    throw;
                }
            },
            fallback: async () => jobIds.ToDictionary(id => id, _ => false),
            operationName: "TryAcquireLocksBulk",
            cancellationToken: cancellationToken
        );

    /// <summary>
    /// Releases multiple locks atomically using Lua script (bulk optimization).
    /// </summary>
    public Task<int> ReleaseLocksBulkAsync(List<Guid> jobIds, string workerId, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    const string bulkReleaseScript = @"
                        local released = 0
                        local owner = ARGV[1]
                        for i, key in ipairs(KEYS) do
                            if redis.call('GET', key) == owner then
                                redis.call('DEL', key)
                                released = released + 1
                            end
                        end
                        return released";

                    var keys = jobIds.Select(id => (RedisKey)_options.GetLockKey(id)).ToArray();
                    var args = new RedisValue[] { workerId };
                    var result = await _database.ScriptEvaluateAsync(bulkReleaseScript, keys, args);
                    var releasedCount = (int)result;

                    _logger.Debug("Bulk lock release: {Released}/{Total} locks released by {WorkerId}", releasedCount, jobIds.Count, workerId);

                    return releasedCount;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error during bulk lock release for {Count} jobs", jobIds.Count);
                    throw;
                }
            },
            fallback: async () => 0,
            operationName: "ReleaseLocksBulk",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<bool> TryAcquireNamedLockAsync(string lockName, string workerId, TimeSpan ttl, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    var lockKey = $"{_options.KeyPrefix}global_lock:{lockName}";

                    // SET key value NX EX ttl - atomic acquire
                    var acquired = await _database.StringSetAsync(lockKey, workerId, ttl, when: When.NotExists);

                    if (acquired)
                    {
                        _logger.Information("Named lock '{LockName}' acquired by instance {WorkerId} (TTL: {Ttl}s)", lockName, workerId, (int)ttl.TotalSeconds);
                    }
                    else
                    {
                        var currentOwner = await _database.StringGetAsync(lockKey);
                        _logger.Debug("Named lock '{LockName}' already held by {CurrentOwner}, skipping operation", lockName, currentOwner);
                    }

                    return acquired;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error acquiring named lock '{LockName}'", lockName);
                    throw;
                }
            },
            fallback: async () => false,
            operationName: "TryAcquireNamedLock",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<bool> ReleaseNamedLockAsync(string lockName, string workerId, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    var lockKey = $"{_options.KeyPrefix}global_lock:{lockName}";

                    var result = await _database.ScriptEvaluateAsync(_checkOwnerAndDeleteScript, [lockKey], [workerId]);

                    var released = (int)result == 1;

                    if (released)
                    {
                        _logger.Information("Named lock '{LockName}' released by instance {WorkerId}", lockName, workerId);
                    }

                    return released;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error releasing named lock '{LockName}'", lockName);
                    throw;
                }
            },
            fallback: async () => false,
            operationName: "ReleaseNamedLock",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<bool> ExtendNamedLockAsync(string lockName, string workerId, TimeSpan ttl, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                var result = await _database.ScriptEvaluateAsync(_checkOwnerAndExtendTTLScript, [$"{_options.KeyPrefix}global_lock:{lockName}"], [workerId, (int)ttl.TotalSeconds]);

                return (int)result == 1;
            },
            fallback: async () => false,
            operationName: "ExtendNamedLock",
            cancellationToken: cancellationToken
        );
}

