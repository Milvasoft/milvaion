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
                        _logger.Information("Lock released for job {JobId} by worker {WorkerId}", jobId, workerId);
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
}
