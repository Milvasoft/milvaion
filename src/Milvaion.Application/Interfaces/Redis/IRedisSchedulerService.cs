namespace Milvaion.Application.Interfaces.Redis;

/// <summary>
/// Redis-based job scheduler using ZSET for time-ordered scheduling.
/// </summary>
public interface IRedisSchedulerService
{
    /// <summary>
    /// Adds a job to the scheduled jobs ZSET.
    /// </summary>
    /// <param name="jobId">Job identifier</param>
    /// <param name="executeAt">Scheduled execution time (score = Unix timestamp)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if added, false if already exists</returns>
    Task<bool> AddToScheduledSetAsync(Guid jobId, DateTime executeAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets job IDs that are due for execution (score smaller or equal to NOW).
    /// </summary>
    /// <param name="now">Current time</param>
    /// <param name="limit">Maximum number of jobs to retrieve</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of due job IDs</returns>
    Task<List<Guid>> GetDueJobsAsync(DateTime now, int limit = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets jobs scheduled within a time range, ordered by execution time.
    /// </summary>
    /// <remarks>
    /// The ZSET holds one entry per job - the next execution only - so this returns
    /// each job at most once. It is the live schedule: the <c>ExecuteAt</c> column in
    /// the database is the originally configured start time and goes stale as soon as
    /// a recurring job runs for the first time.
    /// </remarks>
    /// <param name="from">Range start, inclusive</param>
    /// <param name="to">Range end, inclusive. Null means no upper bound</param>
    /// <param name="limit">Maximum number of jobs to retrieve</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Job identifiers mapped to their scheduled execution time, earliest first</returns>
    Task<List<KeyValuePair<Guid, DateTime>>> GetScheduledJobsInRangeAsync(DateTime from, DateTime? to = null, int limit = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a job from the scheduled jobs ZSET.
    /// </summary>
    /// <param name="jobId">Job identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if removed, false if not found</returns>
    Task<bool> RemoveFromScheduledSetAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes multiple jobs from the scheduled jobs ZSET in bulk (pipeline).
    /// </summary>
    /// <param name="jobIds">Job identifiers to remove</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of jobs removed</returns>
    Task<long> RemoveFromScheduledSetBulkAsync(IEnumerable<Guid> jobIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the scheduled execution time for a job.
    /// </summary>
    /// <param name="jobId">Job identifier</param>
    /// <param name="newExecuteAt">New execution time</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if updated, false if job not found</returns>
    Task<bool> UpdateScheduleAsync(Guid jobId, DateTime newExecuteAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the scheduled execution time for a specific job.
    /// </summary>
    /// <param name="jobId">Job identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Scheduled execution time, or null if not found</returns>
    Task<DateTime?> GetScheduledTimeAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets scheduled execution times for multiple jobs in bulk using Redis pipeline.
    /// Efficiently retrieves ExecuteAt values from ZSET in a single round-trip.
    /// </summary>
    /// <param name="jobIds">Job identifiers to retrieve ExecuteAt values for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary mapping JobId to ExecuteAt timestamp (null if not found in ZSET)</returns>
    Task<Dictionary<Guid, DateTime?>> GetScheduledTimesBulkAsync(IEnumerable<Guid> jobIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the total count of scheduled jobs in Redis.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Count of scheduled jobs</returns>
    Task<long> GetScheduledJobsCountAsync(CancellationToken cancellationToken = default);

    // ============ JOB CACHING METHODS ============

    /// <summary>
    /// Caches a job's details in Redis Hash for fast retrieval.
    /// Key pattern: job:{jobId}
    /// </summary>
    /// <param name="job">Job to cache</param>
    /// <param name="ttl">Time-to-live (default: 24 hours)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if cached successfully</returns>
    Task<bool> CacheJobDetailsAsync(ScheduledJob job, TimeSpan? ttl = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a single job's details from Redis cache.
    /// Returns null if not found in cache (cache miss).
    /// </summary>
    /// <param name="jobId">Job identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Cached job or null</returns>
    Task<ScheduledJob> GetCachedJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets multiple jobs from Redis cache in bulk (optimized with pipeline).
    /// Returns dictionary with jobId as key. Missing jobs are not included.
    /// </summary>
    /// <param name="jobIds">List of job identifiers</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary of cached jobs (jobId ? job)</returns>
    Task<Dictionary<Guid, ScheduledJob>> GetCachedJobsBulkAsync(IEnumerable<Guid> jobIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a job from cache.
    /// </summary>
    /// <param name="jobId">Job identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if removed, false if not found</returns>
    Task<bool> RemoveCachedJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes multiple jobs from cache in bulk (pipeline).
    /// </summary>
    /// <param name="jobIds">Job identifiers to remove</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of jobs removed from cache</returns>
    Task<long> RemoveCachedJobsBulkAsync(IEnumerable<Guid> jobIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates specific fields in cached job (partial update).
    /// </summary>
    /// <param name="jobId">Job identifier</param>
    /// <param name="fieldsToUpdate">Dictionary of field names and values to update</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if updated, false if job not in cache</returns>
    Task<bool> UpdateCachedJobFieldsAsync(Guid jobId, Dictionary<string, object> fieldsToUpdate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the internal job ID for an external job ID from Redis cache.
    /// Returns null if not found in cache.
    /// </summary>
    /// <param name="externalJobId">External job identifier (e.g., "DEFAULT.MyQuartzJob")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Internal job ID or null if not found</returns>
    Task<Guid?> GetJobIdByExternalIdAsync(string externalJobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets internal job IDs for multiple external job IDs in bulk (optimized with pipeline).
    /// Returns dictionary with externalJobId as key. Missing mappings are not included.
    /// </summary>
    /// <param name="externalJobIds">List of external job identifiers</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary of mappings (externalJobId ? jobId)</returns>
    Task<Dictionary<string, Guid>> GetJobIdsByExternalIdsBulkAsync(IEnumerable<string> externalJobIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the mapping from external job ID to internal job ID in Redis.
    /// Used when a new external job (Quartz/Hangfire) is registered.
    /// </summary>
    /// <param name="externalJobId">External job identifier (e.g., "Samples.SampleLogJob")</param>
    /// <param name="jobId">Internal job identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SetExternalJobIdMappingAsync(string externalJobId, Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets multiple external job ID to internal job ID mappings in bulk.
    /// </summary>
    /// <param name="mappings">Dictionary of externalJobId ? jobId mappings</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SetExternalJobIdMappingsBulkAsync(Dictionary<string, Guid> mappings, CancellationToken cancellationToken = default);

    // ============ RUNNING JOB TRACKING ============

    /// <summary>
    /// Atomically tries to mark a job as running in Redis (for ConcurrentExecutionPolicy check).
    /// Uses Redis SET SADD which returns true only if the job was not already running.
    /// This prevents race conditions where multiple occurrences could be marked as running simultaneously.
    /// </summary>
    /// <param name="jobId">Job identifier</param>
    /// <param name="correlationId">Occurrence correlation ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if job was successfully marked as running (was not running before), false if already running</returns>
    Task<bool> TryMarkJobAsRunningAsync(Guid jobId, Guid correlationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a job as running in Redis (for ConcurrentExecutionPolicy check).
    /// Uses a SET to track running job IDs.
    /// Note: This is non-atomic. Use TryMarkJobAsRunningAsync for atomic check-and-set.
    /// </summary>
    /// <param name="jobId">Job identifier</param>
    /// <param name="correlationId">Occurrence correlation ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task MarkJobAsRunningAsync(Guid jobId, Guid correlationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a job as completed/stopped in Redis.
    /// Removes from running jobs SET.
    /// </summary>
    /// <param name="jobId">Job identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if actually removed from Redis. False if circuit breaker fallback was used (Redis unavailable).</returns>
    Task<bool> MarkJobAsCompletedAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a job is currently running.
    /// </summary>
    /// <param name="jobId">Job identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if job has running occurrence</returns>
    Task<bool> IsJobRunningAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all running job IDs from a given set of job IDs.
    /// Optimized bulk check using Redis SET operations.
    /// </summary>
    /// <param name="jobIds">Job IDs to check</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Set of job IDs that are currently running</returns>
    Task<HashSet<Guid>> GetRunningJobIdsAsync(IEnumerable<Guid> jobIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks all running jobs that are associated with the given worker as completed in Redis.
    /// This is used on worker shutdown to remove running flags for jobs that were owned by this worker.
    /// </summary>
    /// <param name="workerId">Worker identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<long> RemoveAllRunningJobsForWorkerAsync(string workerId, CancellationToken cancellationToken = default);
}
