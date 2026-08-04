using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces.Redis;
using Milvaion.Application.Utils.Models.Options;
using Milvaion.Infrastructure.Services.Redis;
using Milvaion.IntegrationTests.TestBase;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.Services;

/// <summary>
/// Base class for Redis service integration tests.
/// Provides access to Redis services and cleanup utilities.
/// </summary>
public abstract class RedisServiceTestBase(CustomWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    /// <summary>
    /// The key prefix the services under test are configured with.
    ///
    /// Every key the services write is namespaced with it, so a test that reaches into Redis directly has to
    /// build its keys the same way. Read from configuration rather than repeated as a literal: a test holding
    /// its own copy of the prefix would keep passing against a key nothing else uses if the setting changed.
    /// </summary>
    protected string RedisKeyPrefix => _serviceProvider.GetRequiredService<IOptions<RedisOptions>>().Value.KeyPrefix;

    /// <summary>
    /// Gets the Redis connection multiplexer.
    /// </summary>
    protected IConnectionMultiplexer GetRedisConnection() => _serviceProvider.GetRequiredService<IConnectionMultiplexer>();

    /// <summary>
    /// Gets the Redis database instance.
    /// </summary>
    protected IDatabase GetRedisDatabase() => GetRedisConnection().GetDatabase();

    /// <summary>
    /// Gets the Redis lock service.
    /// </summary>
    protected IRedisLockService GetRedisLockService() => _serviceProvider.GetRequiredService<IRedisLockService>();

    /// <summary>
    /// Gets the Redis scheduler service.
    /// </summary>
    protected IRedisSchedulerService GetRedisSchedulerService() => _serviceProvider.GetRequiredService<IRedisSchedulerService>();

    /// <summary>
    /// Gets the Redis stats service.
    /// </summary>
    protected IRedisStatsService GetRedisStatsService() => _serviceProvider.GetRequiredService<IRedisStatsService>();

    /// <summary>
    /// Gets the Redis worker service.
    /// </summary>
    protected IRedisWorkerService GetRedisWorkerService() => _serviceProvider.GetRequiredService<IRedisWorkerService>();

    /// <summary>
    /// Gets the Redis cancellation service.
    /// </summary>
    protected IRedisCancellationService GetRedisCancellationService() => _serviceProvider.GetRequiredService<IRedisCancellationService>();

    /// <summary>
    /// Gets the Redis connection service.
    /// </summary>
    protected RedisConnectionService GetRedisConnectionService() => _serviceProvider.GetRequiredService<RedisConnectionService>();

    /// <summary>
    /// Deletes all keys in the current Redis database to ensure clean test state.
    /// Uses SCAN + DEL instead of FLUSHDB to avoid requiring admin mode.
    /// </summary>
    protected async Task FlushRedisAsync()
    {
        var db = GetRedisDatabase();
        var server = GetRedisConnection().GetServers().First();

        var keys = new List<RedisKey>();

        await foreach (var key in server.KeysAsync(database: db.Database, pattern: "*", pageSize: 500))
            keys.Add(key);

        if (keys.Count > 0)
            await db.KeyDeleteAsync([.. keys]);
    }
}
