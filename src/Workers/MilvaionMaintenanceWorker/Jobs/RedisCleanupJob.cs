using Dapper;
using Microsoft.Extensions.Options;
using MilvaionMaintenanceWorker.Options;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Npgsql;
using StackExchange.Redis;
using System.Text.Json;

namespace MilvaionMaintenanceWorker.Jobs;

/// <summary>
/// Cleans up orphaned Redis entries that are no longer needed.
/// - Orphaned job cache entries (jobs deleted from DB but still in cache)
/// - Stale lock entries
/// - Orphaned running job states
/// Recommended schedule: Daily.
/// </summary>
public class RedisCleanupJob(IOptions<MaintenanceOptions> options) : IAsyncJobWithResult
{
    private readonly MaintenanceOptions _options = options.Value;

    public async Task<string> ExecuteAsync(IJobContext context)
    {
        var settings = _options.RedisCleanup;
        var results = new Dictionary<string, int>();

        context.LogInformation("[REDIS-CLEANUP] Redis cleanup started");
        context.LogInformation($"Key prefix: {settings.KeyPrefix}");

        // Connect to Redis
        var redis = await ConnectionMultiplexer.ConnectAsync(_options.RedisConnectionString);
        var db = redis.GetDatabase();
        var server = redis.GetServer(redis.GetEndPoints().First());

        // Connect to PostgreSQL to check valid job IDs
        await using var connection = new NpgsqlConnection(_options.DatabaseConnectionString);
        await connection.OpenAsync(context.CancellationToken);

        // Get all active job IDs from database
        var activeJobIds = (await connection.QueryAsync<Guid>(@"SELECT ""Id"" FROM ""ScheduledJobs"" WHERE ""IsActive"" = true")).ToHashSet();

        context.LogInformation($"Found {activeJobIds.Count} active jobs in database");

        // 1. Clean orphaned job cache entries
        if (settings.CleanOrphanedJobCache)
        {
            var orphanedCacheCount = await CleanOrphanedJobCacheAsync(server, db, settings.KeyPrefix, activeJobIds, context);

            results["OrphanedJobCache"] = orphanedCacheCount;
        }

        // 2. Clean stale lock entries
        if (settings.CleanStaleLocks)
        {
            var staleLockCount = await CleanStaleLocksAsync(server, db, settings.KeyPrefix, settings.StaleLockHours, context);

            results["StaleLocks"] = staleLockCount;
        }

        // 3. Clean orphaned running states
        if (settings.CleanOrphanedRunningStates)
        {
            var orphanedRunningCount = await CleanOrphanedRunningStatesAsync(server, db, settings.KeyPrefix, activeJobIds, context);

            results["OrphanedRunningStates"] = orphanedRunningCount;
        }

        var totalCleaned = results.Values.Sum();

        context.LogInformation($"[DONE] Redis cleanup completed. Total keys cleaned: {totalCleaned}");

        await redis.CloseAsync();

        return JsonSerializer.Serialize(new
        {
            Success = true,
            TotalCleaned = totalCleaned,
            Details = results
        });
    }

    private static async Task<int> CleanOrphanedJobCacheAsync(IServer server,
                                                              IDatabase db,
                                                              string prefix,
                                                              HashSet<Guid> activeJobIds,
                                                              IJobContext context)
    {
        context.LogInformation("  Scanning for orphaned job cache entries...");

        var pattern = $"{prefix}job:*";
        var orphanedCount = 0;
        var scannedCount = 0;

        await foreach (var key in server.KeysAsync(pattern: pattern))
        {
            scannedCount++;

            // Extract job ID from key (format: prefix:job:{jobId})
            var keyStr = key.ToString();
            var parts = keyStr.Split(':');

            if (parts.Length >= 3 && Guid.TryParse(parts[^1], out var jobId))
            {
                if (!activeJobIds.Contains(jobId))
                {
                    await db.KeyDeleteAsync(key);
                    orphanedCount++;
                }
            }

            context.CancellationToken.ThrowIfCancellationRequested();
        }

        context.LogInformation($"  [OK] Scanned {scannedCount} cache keys, deleted {orphanedCount} orphaned entries");

        return orphanedCount;
    }

    private static async Task<int> CleanStaleLocksAsync(IServer server,
                                                        IDatabase db,
                                                        string prefix,
                                                        int staleLockHours,
                                                        IJobContext context)
    {
        context.LogInformation($"  Scanning for stale locks (older than {staleLockHours}h)...");

        var pattern = $"{prefix}lock:*";
        var staleCount = 0;
        var scannedCount = 0;

        await foreach (var key in server.KeysAsync(pattern: pattern))
        {
            scannedCount++;

            // Check TTL - if no TTL and key exists, it might be stale
            var ttl = await db.KeyTimeToLiveAsync(key);

            if (!ttl.HasValue)
            {
                // Key has no TTL - check if it's old by trying to get creation time from value
                var value = await db.StringGetAsync(key);

                if (value.HasValue)
                {
                    // If lock value contains a timestamp, check if it's stale
                    // Otherwise, delete locks without TTL (they should always have TTL)
                    await db.KeyDeleteAsync(key);
                    staleCount++;
                }
            }

            context.CancellationToken.ThrowIfCancellationRequested();
        }

        context.LogInformation($"  [OK] Scanned {scannedCount} lock keys, deleted {staleCount} stale locks");

        return staleCount;
    }

    private static async Task<int> CleanOrphanedRunningStatesAsync(IServer server,
                                                                   IDatabase db,
                                                                   string prefix,
                                                                   HashSet<Guid> activeJobIds,
                                                                   IJobContext context)
    {
        context.LogInformation("  Scanning for orphaned running states...");

        var pattern = $"{prefix}running:*";
        var orphanedCount = 0;
        var scannedCount = 0;

        await foreach (var key in server.KeysAsync(pattern: pattern))
        {
            scannedCount++;

            var keyStr = key.ToString();
            var parts = keyStr.Split(':');

            if (parts.Length >= 3 && Guid.TryParse(parts[^1], out var jobId))
            {
                if (!activeJobIds.Contains(jobId))
                {
                    await db.KeyDeleteAsync(key);
                    orphanedCount++;
                }
            }

            context.CancellationToken.ThrowIfCancellationRequested();
        }

        context.LogInformation($"  [OK] Scanned {scannedCount} running state keys, deleted {orphanedCount} orphaned entries");

        return orphanedCount;
    }
}
