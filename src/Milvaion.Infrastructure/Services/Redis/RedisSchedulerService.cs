using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces.Redis;
using Milvasoft.Core.Abstractions;
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
    public Task<bool> AddToScheduledSetAsync(Guid jobId, DateTime executeAt, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    var score = GetUnixTimestamp(executeAt);

                    var added = await _database.SortedSetAddAsync(_options.ScheduledJobsKey, jobId.ToString(), score);

                    if (added)
                        _logger.Warning("Job {JobId} added to Redis ZSET with score {Score} (ExecuteAt: {ExecuteAt})", jobId, score, executeAt);

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

                if (jobIdsList.Count == 0)
                    return 0L;

                try
                {
                    // Convert GUIDs to RedisValue array
                    var redisValues = jobIdsList.Select(id => (RedisValue)id.ToString()).ToArray();

                    // Single ZREM command with multiple members
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

                    _logger.Information("Job {JobId} schedule updated to {NewExecuteAt} (score: {NewScore})", jobId, newExecuteAt, newScore);

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

                if (jobIdsList.Count == 0)
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
                        new("JobType", job.JobNameInWorker),
                        new("JobData", job.JobData ?? string.Empty),
                        new("CronExpression", job.CronExpression ?? string.Empty),
                        new("IsActive", job.IsActive.ToString()),
                        new("ConcurrentExecutionPolicy", ((int)job.ConcurrentExecutionPolicy).ToString()),
                        new("WorkerId", job.WorkerId ?? string.Empty),
                        new("RoutingPattern", job.RoutingPattern ?? string.Empty), // ADDED
                        new("ZombieTimeoutMinutes", job.ZombieTimeoutMinutes?.ToString() ?? string.Empty),
                        new("ExecutionTimeoutSeconds", job.ExecutionTimeoutSeconds?.ToString() ?? string.Empty),
                        new("Version", job.Version.ToString()),
                        new("CreationDate", job.CreationDate?.ToString("O") ?? string.Empty),
                        new("CreatorUserName", job.CreatorUserName ?? string.Empty)
                    };

                    // HMSET + EXPIRE in transaction
                    var transaction = _database.CreateTransaction();
                    var setTask = transaction.HashSetAsync(cacheKey, hashEntries);
                    var expireTask = transaction.KeyExpireAsync(cacheKey, expiry);

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

                if (jobIdsList.Count == 0)
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

                if (jobIdsList.Count == 0)
                    return 0L;

                try
                {
                    // Convert to RedisKey array
                    var cacheKeys = jobIdsList.Select(id => (RedisKey)GetJobCacheKey(id)).ToArray();

                    // Single DEL command with multiple keys
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

    /// <summary>
    /// Gets the Redis cache key for a job.
    /// </summary>
    private string GetJobCacheKey(Guid jobId) => $"{_options.KeyPrefix}job:{jobId}";

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
                ZombieTimeoutMinutes = dict.TryGetValue("ZombieTimeoutMinutes", out var timeoutStr) && int.TryParse(timeoutStr, out var timeout)
                    ? timeout
                    : null,
                ExecutionTimeoutSeconds = dict.TryGetValue("ExecutionTimeoutSeconds", out var execTimeoutStr) && int.TryParse(execTimeoutStr, out var execTimeout)
                    ? execTimeout
                    : null,
                Version = dict.TryGetValue("Version", out var versionStr) && int.TryParse(versionStr, out var version)
                    ? version
                    : 1,
                CreationDate = string.IsNullOrEmpty(dict.GetValueOrDefault("CreationDate")) ? null : DateTime.Parse(dict["CreationDate"]),
                CreatorUserName = string.IsNullOrEmpty(dict.GetValueOrDefault("CreatorUserName")) ? null : dict["CreatorUserName"]
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
                    // SADD returns true if added (wasn't in set), false if already exists
                    // This is ATOMIC - prevents race conditions
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
    public Task MarkJobAsCompletedAsync(Guid jobId, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    await _database.SetRemoveAsync(RunningJobsKey, jobId.ToString());

                    _logger.Debug("Job {JobId} marked as completed", jobId);

                    return true; // Dummy return
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to mark job {JobId} as completed", jobId);
                    throw;
                }
            },
            fallback: async () => true, // void method için dummy fallback
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

                if (jobIdsList.Count == 0)
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
