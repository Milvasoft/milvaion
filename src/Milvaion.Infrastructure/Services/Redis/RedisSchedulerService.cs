using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces.Redis;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Helpers;
using Milvasoft.Milvaion.Sdk.Utils;
using StackExchange.Redis;

namespace Milvaion.Infrastructure.Services.Redis;

/// <summary>
/// Redis-based job scheduler implementation using ZSET.
/// </summary>
public class RedisSchedulerService : IRedisSchedulerService
{
    private readonly RedisConnectionService _redisConnection;
    private readonly RedisOptions _options;
    private readonly IMilvaLogger _logger;
    private readonly IDatabase _database;
    private readonly IRedisCircuitBreaker _circuitBreaker;

    /// <summary>
    /// Gets the Redis key for running jobs SET.
    /// </summary>
    private string RunningJobsKey => $"{_options.KeyPrefix}running_jobs";

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisSchedulerService"/> class.
    /// </summary>
    public RedisSchedulerService(RedisConnectionService redisConnection,
                                 IOptions<RedisOptions> options,
                                 IRedisCircuitBreaker circuitBreaker,
                                 ILoggerFactory loggerFactory)
    {
        _redisConnection = redisConnection;
        _options = options.Value;
        _circuitBreaker = circuitBreaker;
        _logger = loggerFactory.CreateMilvaLogger<RedisSchedulerService>();
        _database = _redisConnection.Database;
    }

    /// <inheritdoc/>
    public Task<long> RemoveAllRunningJobsForWorkerAsync(string workerId, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(workerId))
                        return 0L;

                    // Strategy: We store cached job details keyed by job:{jobId}. Each cached job contains WorkerId.
                    // Scan known cached keys (approx) is expensive. Instead, we maintain a separate Redis set which maps workerId -> running jobIds.
                    // If such set does not exist, fallback to scanning running_jobs and try to inspect cached job details.

                    var workerRunningKey = $"{_options.KeyPrefix}running_jobs_by_worker:{workerId}";

                    // If worker-specific set exists, remove those members from global running set and return count
                    if (await _database.KeyExistsAsync(workerRunningKey))
                    {
                        var members = new List<RedisValue>();

                        // Use SSCAN to avoid loading all members into memory at once
                        await foreach (var member in _database.SetScanAsync(workerRunningKey, pageSize: 100))
                        {
                            members.Add(member);
                        }

                        if (members.IsNullOrEmpty())
                        {
                            await _database.KeyDeleteAsync(workerRunningKey);
                            return 0L;
                        }

                        // Remove each from global running set using pipeline
                        var redisValues = members.Select(m => (RedisValue)m.ToString()).ToArray();
                        var removed = await _database.SetRemoveAsync(RunningJobsKey, redisValues);

                        // Delete worker-specific set
                        await _database.KeyDeleteAsync(workerRunningKey);

                        _logger.Debug("Removed {Count} running jobs for worker {WorkerId} from Redis running set", removed, workerId);
                        return removed;
                    }

                    // Fallback: iterate running_jobs set using SSCAN and check cached job details for workerId match
                    var toRemove = new List<RedisValue>();

                    await foreach (var member in _database.SetScanAsync(RunningJobsKey, pageSize: 100))
                    {
                        if (!Guid.TryParse(member.ToString(), out var jobId))
                            continue;

                        // Cached job key pattern: job:{jobId}
                        var cachedKey = $"{_options.KeyPrefix}job:{jobId}";

                        if (!await _database.KeyExistsAsync(cachedKey))
                            continue;

                        var workerField = await _database.HashGetAsync(cachedKey, "WorkerId");

                        if (!workerField.IsNullOrEmpty && workerField.ToString() == workerId)
                        {
                            toRemove.Add(member);
                        }
                    }

                    if (!toRemove.IsNullOrEmpty())
                    {
                        var removed = await _database.SetRemoveAsync(RunningJobsKey, [.. toRemove]);
                        _logger.Debug("Fallback removed {Count} running jobs for worker {WorkerId}", removed, workerId);
                        return removed;
                    }

                    return 0L;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to clean running jobs for worker {WorkerId}", workerId);
                    return 0L;
                }
            },
            fallback: async () => 0L,
            operationName: "RemoveAllRunningJobsForWorkerAsync",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<bool> AddToScheduledSetAsync(Guid jobId, DateTime executeAt, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    var score = GetUnixTimestamp(executeAt);

                    var added = await _database.SortedSetAddAsync(_options.ScheduledJobsKey, jobId.ToString(), score);

                    if (added)
                        _logger.Debug("Job {JobId} added to Redis ZSET with score {Score} (ExecuteAt: {ExecuteAt})", jobId, score, executeAt);

                    return added;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to add job {JobId} to Redis ZSET", jobId);
                    throw;
                }
            },
            fallback: async () => false,
            operationName: "AddToScheduledSet",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<List<Guid>> GetDueJobsAsync(DateTime now, int limit = 100, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    var maxScore = GetUnixTimestamp(now);

                    // ZRANGEBYSCORE scheduled_jobs -inf {NOW} LIMIT 0 100
                    var values = await _database.SortedSetRangeByScoreAsync(_options.ScheduledJobsKey,
                                                                            start: double.NegativeInfinity,
                                                                            stop: maxScore,
                                                                            take: limit,
                                                                            order: Order.Ascending);

                    var jobIds = values.Where(v => v.HasValue && Guid.TryParse(v.ToString(), out _))
                                       .Select(v => Guid.Parse(v.ToString()))
                                       .ToList();

                    _logger.Debug("Retrieved {Count} due jobs from Redis (maxScore: {MaxScore}, now: {Now})", jobIds.Count, maxScore, now);

                    return jobIds;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to get due jobs from Redis");
                    throw;
                }
            },
            fallback: async () => [],
            operationName: "GetDueJobs",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<List<KeyValuePair<Guid, DateTime>>> GetScheduledJobsInRangeAsync(DateTime from, DateTime? to = null, int limit = 100, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    var minScore = GetUnixTimestamp(from);
                    var maxScore = to.HasValue ? GetUnixTimestamp(to.Value) : double.PositiveInfinity;

                    // ZRANGEBYSCORE scheduled_jobs {FROM} {TO} WITHSCORES LIMIT 0 {LIMIT}
                    var entries = await _database.SortedSetRangeByScoreWithScoresAsync(_options.ScheduledJobsKey,
                                                                                       start: minScore,
                                                                                       stop: maxScore,
                                                                                       take: limit,
                                                                                       order: Order.Ascending);

                    var result = new List<KeyValuePair<Guid, DateTime>>(entries.Length);

                    foreach (var entry in entries)
                    {
                        if (!entry.Element.HasValue || !Guid.TryParse(entry.Element.ToString(), out var jobId))
                            continue;

                        var scheduledAt = DateTimeOffset.FromUnixTimeSeconds((long)entry.Score).UtcDateTime;

                        result.Add(new KeyValuePair<Guid, DateTime>(jobId, scheduledAt));
                    }

                    _logger.Debug("Retrieved {Count} scheduled jobs from Redis in range {From} - {To}", result.Count, from, to);

                    return result;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to get scheduled jobs in range from Redis");
                    throw;
                }
            },
            fallback: async () => [],
            operationName: "GetScheduledJobsInRange",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<bool> RemoveFromScheduledSetAsync(Guid jobId, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    var removed = await _database.SortedSetRemoveAsync(_options.ScheduledJobsKey, jobId.ToString());

                    if (removed)
                        _logger.Debug("Job {JobId} removed from Redis ZSET", jobId);

                    return removed;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to remove job {JobId} from Redis", jobId);
                    throw;
                }
            },
            fallback: async () => false,
            operationName: "RemoveFromScheduledSet",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<long> RemoveFromScheduledSetBulkAsync(IEnumerable<Guid> jobIds, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                var jobIdsList = jobIds.ToList();

                if (jobIdsList.IsNullOrEmpty())
                    return 0L;

                try
                {
                    // Convert GUIDs to RedisValue array
                    var redisValues = jobIdsList.Select(id => (RedisValue)id.ToString()).ToArray();

                    var removed = await _database.SortedSetRemoveAsync(_options.ScheduledJobsKey, redisValues);

                    _logger.Debug("Removed {Count}/{Total} jobs from Redis ZSET (bulk)", removed, jobIdsList.Count);

                    return removed;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to bulk remove {Count} jobs from Redis ZSET", jobIdsList.Count);
                    throw;
                }
            },
            fallback: async () => 0L,
            operationName: "RemoveFromScheduledSetBulk",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<bool> UpdateScheduleAsync(Guid jobId, DateTime newExecuteAt, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    var newScore = GetUnixTimestamp(newExecuteAt);

                    // ZADD updates the score if member exists
                    var updated = await _database.SortedSetAddAsync(_options.ScheduledJobsKey, jobId.ToString(), newScore);

                    _logger.Debug("Job {JobId} schedule updated to {NewExecuteAt} (score: {NewScore})", jobId, newExecuteAt, newScore);

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to update schedule for job {JobId}", jobId);
                    throw;
                }
            },
            fallback: async () => false,
            operationName: "UpdateSchedule",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<DateTime?> GetScheduledTimeAsync(Guid jobId, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync<DateTime?>(
            operation: async () =>
            {
                try
                {
                    var score = await _database.SortedSetScoreAsync(_options.ScheduledJobsKey, jobId.ToString());

                    if (!score.HasValue)
                        return null;

                    return DateTimeOffset.FromUnixTimeSeconds((long)score.Value).UtcDateTime;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to get scheduled time for job {JobId}", jobId);
                    throw;
                }
            },
            fallback: async () => null,
            operationName: "GetScheduledTime",
            cancellationToken: cancellationToken
        );

    /// <summary>
    /// Gets scheduled times (ExecuteAt) for multiple jobs in bulk using Redis pipeline.
    /// </summary>
    /// <param name="jobIds">Job IDs to retrieve ExecuteAt values for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Dictionary mapping JobId to ExecuteAt timestamp (null if not found in ZSET).</returns>
    public Task<Dictionary<Guid, DateTime?>> GetScheduledTimesBulkAsync(IEnumerable<Guid> jobIds, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                var result = new Dictionary<Guid, DateTime?>();
                var jobIdsList = jobIds.ToList();

                if (jobIdsList.IsNullOrEmpty())
                    return result;

                try
                {
                    // PIPELINE: Fire all SortedSetScore commands at once
                    var tasks = new List<(Guid JobId, Task<double?> Task)>();

                    foreach (var jobId in jobIdsList)
                    {
                        tasks.Add((jobId, _database.SortedSetScoreAsync(_options.ScheduledJobsKey, jobId.ToString())));
                    }

                    // Wait for all pipeline commands to complete
                    await Task.WhenAll(tasks.Select(t => t.Task));

                    // Convert scores to DateTime
                    foreach (var (jobId, task) in tasks)
                    {
                        var score = await task;

                        result[jobId] = score.HasValue
                            ? DateTimeOffset.FromUnixTimeSeconds((long)score.Value).UtcDateTime
                            : null;
                    }

                    _logger.Debug("Pipeline retrieved ExecuteAt for {Count} jobs from ZSET", jobIdsList.Count);

                    return result;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to get scheduled times in bulk");
                    throw;
                }
            },
            fallback: async () => [],
            operationName: "GetScheduledTimesBulk",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<long> GetScheduledJobsCountAsync(CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    return await _database.SortedSetLengthAsync(_options.ScheduledJobsKey);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to get scheduled jobs count");
                    throw;
                }
            },
            fallback: async () => 0L,
            operationName: "GetScheduledJobsCount",
            cancellationToken: cancellationToken
        );

    /// <summary>
    /// Converts DateTime to Unix timestamp (seconds since epoch).
    /// </summary>
    private static double GetUnixTimestamp(DateTime dateTime) => new DateTimeOffset(dateTime.ToUniversalTime()).ToUnixTimeSeconds();

    #region Job Caching

    /// <inheritdoc/>
    public Task<bool> CacheJobDetailsAsync(ScheduledJob job, TimeSpan? ttl = null, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    var cacheKey = GetJobCacheKey(job.Id);
                    var expiry = ttl ?? TimeSpan.FromHours(24); // Default 24 hours

                    // Serialize job to Hash entries
                    var hashEntries = new HashEntry[]
                    {
                        new("Id", job.Id.ToString()),
                        new("DisplayName", job.DisplayName ?? string.Empty),
                        new("Description", job.Description ?? string.Empty),
                        new("Tags", job.Tags ?? string.Empty),
                        new("JobType", job.JobNameInWorker ?? string.Empty),
                        new("JobData", job.JobData ?? string.Empty),
                        new("CronExpression", job.CronExpression ?? string.Empty),
                        new("IsActive", job.IsActive.ToString()),
                        new("ConcurrentExecutionPolicy", ((int)job.ConcurrentExecutionPolicy).ToString()),
                        new("WorkerId", job.WorkerId ?? string.Empty),
                        new("RoutingPattern", job.RoutingPattern ?? string.Empty),
                        new("ZombieTimeoutMinutes", job.ZombieTimeoutMinutes?.ToString() ?? string.Empty),
                        new("ExecutionTimeoutSeconds", job.ExecutionTimeoutSeconds?.ToString() ?? string.Empty),
                        new("Version", job.Version.ToString()),
                        new("CreationDate", job.CreationDate?.ToString("O") ?? string.Empty),
                        new("CreatorUserName", job.CreatorUserName ?? string.Empty),
                        new("ExecuteAt", job.ExecuteAt.ToString("O")),
                        new("IsExternal", job.IsExternal.ToString()),
                        new("ExternalJobId", job.ExternalJobId ?? string.Empty)
                    };

                    // HMSET + EXPIRE in transaction
                    var transaction = _database.CreateTransaction();
                    var setTask = transaction.HashSetAsync(cacheKey, hashEntries);
                    var expireTask = transaction.KeyExpireAsync(cacheKey, expiry);

                    // For external jobs, also create ExternalJobId ? JobId mapping
                    if (job.IsExternal && !string.IsNullOrEmpty(job.ExternalJobId))
                    {
                        var externalMappingKey = GetExternalJobMappingKey(job.ExternalJobId);
                        _ = transaction.StringSetAsync(externalMappingKey, job.Id.ToString(), expiry);
                    }

                    var committed = await transaction.ExecuteAsync();

                    if (committed)
                        _logger.Debug("Job {JobId} cached in Redis with TTL {Ttl}", job.Id, expiry);

                    return committed;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to cache job {JobId}", job.Id);
                    return false;
                }
            },
            fallback: async () => false,
            operationName: "CacheJobDetails",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<ScheduledJob> GetCachedJobAsync(Guid jobId, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    var cacheKey = GetJobCacheKey(jobId);

                    var hashEntries = await _database.HashGetAllAsync(cacheKey);

                    if (hashEntries.Length == 0)
                        return null; // Cache miss

                    return DeserializeJob(hashEntries);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to get cached job {JobId}", jobId);
                    return null;
                }
            },
            fallback: async () => null,
            operationName: "GetCachedJob",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<Dictionary<Guid, ScheduledJob>> GetCachedJobsBulkAsync(IEnumerable<Guid> jobIds, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                var result = new Dictionary<Guid, ScheduledJob>();
                var jobIdsList = jobIds.ToList();

                if (jobIdsList.IsNullOrEmpty())
                    return result;

                try
                {
                    // PIPELINE: Fire all HashGetAll commands at once
                    var tasks = new List<(Guid JobId, Task<HashEntry[]> Task)>();

                    foreach (var jobId in jobIdsList)
                    {
                        var cacheKey = GetJobCacheKey(jobId);
                        // CommandFlags.FireAndForget allows pipelining
                        tasks.Add((jobId, _database.HashGetAllAsync(cacheKey)));
                    }

                    // ? Wait for all pipeline commands to complete
                    await Task.WhenAll(tasks.Select(t => t.Task));

                    // Deserialize results
                    foreach (var (jobId, task) in tasks)
                    {
                        var hashEntries = await task;

                        if (hashEntries.Length > 0)
                        {
                            var job = DeserializeJob(hashEntries);

                            if (job != null)
                                result[jobId] = job;
                        }
                    }

                    _logger.Debug("Pipeline retrieved {CachedCount}/{TotalCount} jobs from Redis cache", result.Count, jobIdsList.Count);
                    return result;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to get cached jobs in bulk");
                    return result;
                }
            },
            fallback: async () => [],
            operationName: "GetCachedJobsBulk",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<bool> RemoveCachedJobAsync(Guid jobId, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    var cacheKey = GetJobCacheKey(jobId);

                    var deleted = await _database.KeyDeleteAsync(cacheKey);

                    if (deleted)
                        _logger.Debug("Job {JobId} removed from cache", jobId);

                    return deleted;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to remove cached job {JobId}", jobId);
                    return false;
                }
            },
            fallback: async () => false,
            operationName: "RemoveCachedJob",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<long> RemoveCachedJobsBulkAsync(IEnumerable<Guid> jobIds, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                var jobIdsList = jobIds.ToList();

                if (jobIdsList.IsNullOrEmpty())
                    return 0L;

                try
                {
                    // Convert to RedisKey array
                    var cacheKeys = jobIdsList.Select(id => (RedisKey)GetJobCacheKey(id)).ToArray();

                    var deleted = await _database.KeyDeleteAsync(cacheKeys);

                    _logger.Debug("Removed {Count}/{Total} jobs from Redis cache (bulk)", deleted, jobIdsList.Count);

                    return deleted;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to bulk remove {Count} jobs from Redis cache", jobIdsList.Count);
                    return 0L;
                }
            },
            fallback: async () => 0L,
            operationName: "RemoveCachedJobsBulk",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<bool> UpdateCachedJobFieldsAsync(Guid jobId, Dictionary<string, object> fieldsToUpdate, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    var cacheKey = GetJobCacheKey(jobId);

                    // Check if key exists
                    var exists = await _database.KeyExistsAsync(cacheKey);

                    if (!exists)
                        return false;

                    // Convert to HashEntry array
                    var entries = fieldsToUpdate.Select(kvp => new HashEntry(kvp.Key, kvp.Value?.ToString() ?? string.Empty)).ToArray();

                    await _database.HashSetAsync(cacheKey, entries);

                    _logger.Debug("Updated {Count} fields for cached job {JobId}", fieldsToUpdate.Count, jobId);

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to update cached job {JobId}", jobId);
                    return false;
                }
            },
            fallback: async () => false,
            operationName: "UpdateCachedJobFields",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<Guid?> GetJobIdByExternalIdAsync(string externalJobId, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync<Guid?>(
            operation: async () =>
            {
                try
                {
                    if (string.IsNullOrEmpty(externalJobId))
                        return null;

                    var mappingKey = GetExternalJobMappingKey(externalJobId);
                    var value = await _database.StringGetAsync(mappingKey);

                    if (value.IsNullOrEmpty)
                        return null;

                    if (Guid.TryParse(value.ToString(), out var jobId))
                        return jobId;

                    return null;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to get job ID for external job {ExternalJobId}", externalJobId);
                    return null;
                }
            },
            fallback: async () => null,
            operationName: "GetJobIdByExternalId",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<Dictionary<string, Guid>> GetJobIdsByExternalIdsBulkAsync(IEnumerable<string> externalJobIds, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                var result = new Dictionary<string, Guid>();
                var externalIdsList = externalJobIds.Where(id => !string.IsNullOrEmpty(id)).ToList();

                if (externalIdsList.IsNullOrEmpty())
                    return result;

                try
                {
                    // PIPELINE: Fire all StringGet commands at once
                    var tasks = new List<(string ExternalId, Task<RedisValue> Task)>();

                    foreach (var externalId in externalIdsList)
                    {
                        var mappingKey = GetExternalJobMappingKey(externalId);
                        tasks.Add((externalId, _database.StringGetAsync(mappingKey)));
                    }

                    await Task.WhenAll(tasks.Select(t => t.Task));

                    foreach (var (externalId, task) in tasks)
                    {
                        var value = await task;
                        if (!value.IsNullOrEmpty && Guid.TryParse(value.ToString(), out var jobId))
                            result[externalId] = jobId;
                    }

                    _logger.Debug("Pipeline retrieved {Count}/{Total} external job mappings from Redis", result.Count, externalIdsList.Count);
                    return result;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to get job IDs for external jobs in bulk");
                    return result;
                }
            },
            fallback: async () => [],
            operationName: "GetJobIdsByExternalIdsBulk",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task SetExternalJobIdMappingAsync(string externalJobId, Guid jobId, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync<bool>(
            operation: async () =>
            {
                try
                {
                    if (string.IsNullOrEmpty(externalJobId))
                        return false;

                    var mappingKey = GetExternalJobMappingKey(externalJobId);
                    await _database.StringSetAsync(mappingKey, jobId.ToString());

                    _logger.Debug("Set external job mapping: {ExternalJobId} → {JobId}", externalJobId, jobId);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to set external job mapping for {ExternalJobId}", externalJobId);
                    return false;
                }
            },
            fallback: async () => false,
            operationName: "SetExternalJobIdMapping",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task SetExternalJobIdMappingsBulkAsync(Dictionary<string, Guid> mappings, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync<bool>(
            operation: async () =>
            {
                if (mappings.IsNullOrEmpty())
                    return true;

                try
                {
                    // PIPELINE: Fire all StringSet commands at once
                    var tasks = new List<Task>();

                    foreach (var (externalId, jobId) in mappings)
                    {
                        if (string.IsNullOrEmpty(externalId))
                            continue;

                        var mappingKey = GetExternalJobMappingKey(externalId);
                        tasks.Add(_database.StringSetAsync(mappingKey, jobId.ToString()));
                    }

                    await Task.WhenAll(tasks);

                    _logger.Debug("Pipeline set {Count} external job mappings in Redis", mappings.Count);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to set external job mappings in bulk");
                    return false;
                }
            },
            fallback: async () => false,
            operationName: "SetExternalJobIdMappingsBulk",
            cancellationToken: cancellationToken
        );

    /// <summary>
    /// Gets the Redis cache key for a job.
    /// </summary>
    private string GetJobCacheKey(Guid jobId) => $"{_options.KeyPrefix}job:{jobId}";

    /// <summary>
    /// Gets the Redis key for external job ID to internal job ID mapping.
    /// </summary>
    private string GetExternalJobMappingKey(string externalJobId) => $"{_options.KeyPrefix}external:{externalJobId}";

    /// <summary>
    /// Deserializes a job from Redis Hash entries.
    /// </summary>
    private ScheduledJob DeserializeJob(HashEntry[] hashEntries)
    {
        try
        {
            var dict = hashEntries.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());

            return new ScheduledJob
            {
                Id = Guid.Parse(dict["Id"]),
                DisplayName = dict.GetValueOrDefault("DisplayName"),
                Description = dict.GetValueOrDefault("Description"),
                Tags = dict.GetValueOrDefault("Tags"),
                JobNameInWorker = dict["JobType"],
                JobData = dict.GetValueOrDefault("JobData"),
                CronExpression = string.IsNullOrEmpty(dict.GetValueOrDefault("CronExpression")) ? null : dict["CronExpression"],
                IsActive = bool.Parse(dict["IsActive"]),
                ConcurrentExecutionPolicy = dict.TryGetValue("ConcurrentExecutionPolicy", out var policyStr) && int.TryParse(policyStr, out var policyInt)
                    ? (ConcurrentExecutionPolicy)policyInt
                    : ConcurrentExecutionPolicy.Skip,
                WorkerId = string.IsNullOrEmpty(dict.GetValueOrDefault("WorkerId")) ? null : dict["WorkerId"],
                RoutingPattern = string.IsNullOrEmpty(dict.GetValueOrDefault("RoutingPattern")) ? null : dict["RoutingPattern"],
                ZombieTimeoutMinutes = dict.TryGetValue("ZombieTimeoutMinutes", out var timeoutStr) && int.TryParse(timeoutStr, out var timeout) ? timeout : null,
                ExecutionTimeoutSeconds = dict.TryGetValue("ExecutionTimeoutSeconds", out var execTimeoutStr) && int.TryParse(execTimeoutStr, out var execTimeout) ? execTimeout : null,
                Version = dict.TryGetValue("Version", out var versionStr) && int.TryParse(versionStr, out var version) ? version : 1,
                CreationDate = string.IsNullOrEmpty(dict.GetValueOrDefault("CreationDate")) ? null : DateTime.Parse(dict["CreationDate"]),
                CreatorUserName = string.IsNullOrEmpty(dict.GetValueOrDefault("CreatorUserName")) ? null : dict["CreatorUserName"],
                ExecuteAt = dict.TryGetValue("ExecuteAt", out var executeAtStr) && DateTime.TryParse(executeAtStr, out var executeAt) ? executeAt : DateTime.MinValue,
                IsExternal = dict.TryGetValue("IsExternal", out var isExternalStr) && bool.TryParse(isExternalStr, out var isExternal) && isExternal,
                ExternalJobId = dict.GetValueOrDefault("ExternalJobId"),
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to deserialize job from Redis Hash");
            return null;
        }
    }

    #endregion

    #region Running Job Tracking

    /// <inheritdoc/>
    public Task<bool> TryMarkJobAsRunningAsync(Guid jobId, Guid correlationId, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    // SADD returns true if added, false if already exists
                    var added = await _database.SetAddAsync(RunningJobsKey, jobId.ToString());

                    if (added)
                    {
                        _logger.Debug("Job {JobId} marked as running (CorrelationId: {CorrelationId})", jobId, correlationId);
                    }
                    else
                    {
                        _logger.Debug("Job {JobId} already running, cannot mark again (CorrelationId: {CorrelationId})", jobId, correlationId);
                    }

                    return added;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to mark job {JobId} as running", jobId);
                    return false;
                }
            },
            fallback: async () => false,
            operationName: "TryMarkJobAsRunning",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task MarkJobAsRunningAsync(Guid jobId, Guid correlationId, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    // Non-atomic version - use TryMarkJobAsRunningAsync for atomic check-and-set
                    await _database.SetAddAsync(RunningJobsKey, jobId.ToString());

                    _logger.Debug("Job {JobId} marked as running (CorrelationId: {CorrelationId})", jobId, correlationId);

                    return true; // Return değeri olmadığı için dummy return
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to mark job {JobId} as running", jobId);
                    throw;
                }
            },
            fallback: async () => true, // void method için dummy fallback
            operationName: "MarkJobAsRunning",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<bool> MarkJobAsCompletedAsync(Guid jobId, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    await _database.SetRemoveAsync(RunningJobsKey, jobId.ToString());

                    _logger.Debug("Job {JobId} marked as completed", jobId);

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to mark job {JobId} as completed", jobId);
                    throw;
                }
            },
            fallback: async () => false, // Circuit breaker open - Redis unavailable
            operationName: "MarkJobAsCompleted",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<bool> IsJobRunningAsync(Guid jobId, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    return await _database.SetContainsAsync(RunningJobsKey, jobId.ToString());
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to check if job {JobId} is running", jobId);
                    return false;
                }
            },
            fallback: async () => false,
            operationName: "IsJobRunning",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<HashSet<Guid>> GetRunningJobIdsAsync(IEnumerable<Guid> jobIds, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                var result = new HashSet<Guid>();
                var jobIdsList = jobIds.ToList();

                if (jobIdsList.IsNullOrEmpty())
                    return result;

                try
                {
                    // ? PIPELINE: Fire all SetContains commands at once
                    var tasks = new List<(Guid JobId, Task<bool> Task)>();

                    foreach (var jobId in jobIdsList)
                        tasks.Add((jobId, _database.SetContainsAsync(RunningJobsKey, jobId.ToString())));

                    // ? Wait for all pipeline commands to complete
                    await Task.WhenAll(tasks.Select(t => t.Task));

                    // Collect results
                    foreach (var (jobId, task) in tasks)
                    {
                        if (await task)
                            result.Add(jobId);
                    }

                    _logger.Debug("Pipeline checked {Total} jobs, {Running} are running", jobIdsList.Count, result.Count);
                    return result;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to get running job IDs");
                    return result;
                }
            },
            fallback: async () => [],
            operationName: "GetRunningJobIds",
            cancellationToken: cancellationToken
        );

    #endregion
}
