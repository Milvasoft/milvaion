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
public class RedisCleanupJob(IOptions<MaintenanceOptions> options) : IAsyncJobWithResult<string>
{
    private readonly MaintenanceOptions _options = options.Value;

    public async Task<string> ExecuteAsync(IJobContext context)
    {
        var settings = _options.RedisCleanup;
        var results = new Dictionary<string, int>();

        context.LogInformation("[REDIS-CLEANUP] Redis cleanup started");
        context.LogInformation($"Key prefix: {settings.KeyPrefix}");

        // Connect to Redis
        await using var redis = await ConnectionMultiplexer.ConnectAsync(_options.RedisConnectionString);
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

            // Locks with a TTL expire on their own
            var ttl = await db.KeyTimeToLiveAsync(key);

            if (!ttl.HasValue && await IsIdleForAsync(db, key, TimeSpan.FromHours(staleLockHours), context))
            {
                await db.KeyDeleteAsync(key);
                staleCount++;
            }

            context.CancellationToken.ThrowIfCancellationRequested();
        }

        context.LogInformation($"  [OK] Scanned {scannedCount} lock keys, deleted {staleCount} stale locks");

        return staleCount;
    }

    /// <summary>
    /// Whether the key has gone untouched for the given time. Lock values only hold an owner id, so the idle time is the
    /// only available age. When Redis cannot report it the lock is kept: wrongly deleting a live lock allows a double dispatch.
    /// </summary>
    private static async Task<bool> IsIdleForAsync(IDatabase db, RedisKey key, TimeSpan threshold, IJobContext context)
    {
        try
        {
            var idleTime = await db.KeyIdleTimeAsync(key);

            return idleTime >= threshold;
        }
        catch (RedisServerException ex)
        {
            context.LogWarning($"  Keeping lock {key}: its idle time is not available ({ex.Message})");

            return false;
        }
    }

    private static async Task<int> CleanOrphanedRunningStatesAsync(IServer server,
                                                                    IDatabase db,
                                                                    string prefix,
                                                                    HashSet<Guid> activeJobIds,
                                                                    IJobContext context)
    {
        context.LogInformation("  Scanning for orphaned running states...");

        var runningJobsKey = $"{prefix}running_jobs";
        var orphanedCount = 0;

        // 1. Clean the global running_jobs SET - remove jobs that are no longer active
        var members = await db.SetMembersAsync(runningJobsKey);

        if (members.Length > 0)
        {
            context.LogInformation($"  Found {members.Length} entries in running_jobs SET");

            var toRemove = new List<RedisValue>();

            foreach (var member in members)
            {
                if (Guid.TryParse(member.ToString(), out var jobId) && !activeJobIds.Contains(jobId))
                {
                    toRemove.Add(member);
                }

                context.CancellationToken.ThrowIfCancellationRequested();
            }

            if (toRemove.Count > 0)
            {
                await db.SetRemoveAsync(runningJobsKey, [.. toRemove]);
                orphanedCount += toRemove.Count;
                context.LogInformation($"  Removed {toRemove.Count} orphaned entries from running_jobs SET");
            }
        }

        // 2. Clean orphaned running_jobs_by_worker:* SETs
        var workerPattern = $"{prefix}running_jobs_by_worker:*";
        var scannedWorkerKeys = 0;
        var orphanedWorkerEntries = 0;

        await foreach (var key in server.KeysAsync(pattern: workerPattern))
        {
            scannedWorkerKeys++;
            var workerMembers = await db.SetMembersAsync(key);
            var workerToRemove = new List<RedisValue>();

            foreach (var member in workerMembers)
            {
                if (Guid.TryParse(member.ToString(), out var jobId) && !activeJobIds.Contains(jobId))
                {
                    workerToRemove.Add(member);
                }
            }

            if (workerToRemove.Count > 0)
            {
                await db.SetRemoveAsync(key, [.. workerToRemove]);
                orphanedWorkerEntries += workerToRemove.Count;
            }

            // Delete empty per-worker SETs
            if (await db.SetLengthAsync(key) == 0)
            {
                await db.KeyDeleteAsync(key);
            }

            context.CancellationToken.ThrowIfCancellationRequested();
        }

        if (orphanedWorkerEntries > 0)
            context.LogInformation($"  Cleaned {orphanedWorkerEntries} orphaned entries from {scannedWorkerKeys} per-worker running SETs");

        context.LogInformation($"  [OK] Total orphaned running states cleaned: {orphanedCount + orphanedWorkerEntries}");

        return orphanedCount + orphanedWorkerEntries;
    }
}
