using Milvasoft.Milvaion.Sdk.Models;

namespace Milvaion.Application.Interfaces.Redis;

/// <summary>
/// Redis-based worker tracking service for runtime state management.
/// Handles worker registration, heartbeats, and capacity tracking.
/// </summary>
public interface IRedisWorkerService
{
    /// <summary>
    /// Registers or updates a worker in Redis.
    /// </summary>
    Task<bool> RegisterWorkerAsync(WorkerDiscoveryRequest registration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates worker heartbeat for specific instance.
    /// </summary>
    Task<bool> UpdateHeartbeatAsync(string workerId, string instanceId, int currentJobs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates heartbeats for multiple worker instances in a single pipeline operation.
    /// Significantly faster than calling UpdateHeartbeatAsync multiple times.
    /// </summary>
    /// <param name="updates">List of worker heartbeat updates (WorkerId, InstanceId, CurrentJobs)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of successfully updated instances</returns>
    Task<int> BulkUpdateHeartbeatsAsync(List<(string WorkerId, string InstanceId, int CurrentJobs)> updates, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets worker information by WorkerId from Redis.
    /// </summary>
    Task<CachedWorker> GetWorkerAsync(string workerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all active workers with their instances from Redis.
    /// </summary>
    Task<List<CachedWorker>> GetAllWorkersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if worker exists and is active.
    /// </summary>
    Task<bool> IsWorkerActiveAsync(string workerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets worker capacity information (current jobs vs max).
    /// </summary>
    Task<(int CurrentJobs, int? MaxParallelJobs)> GetWorkerCapacityAsync(string workerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets consumer-level capacity for a specific job type within a worker group.
    /// Returns current running jobs and max parallel jobs configured for that consumer.
    /// </summary>
    /// <param name="workerId">Worker group ID</param>
    /// <param name="jobType">Job type (e.g., "TestJob", "EmailJob")</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple of (CurrentJobs, MaxParallelJobs for that consumer)</returns>
    Task<(int CurrentJobs, int? MaxParallelJobs)> GetConsumerCapacityAsync(string workerId, string jobType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Increments the running job count for a specific consumer (job type).
    /// Called when a job starts execution.
    /// </summary>
    Task IncrementConsumerJobCountAsync(string workerId, string jobType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decrements the running job count for a specific consumer (job type).
    /// Called when a job completes/fails/times out.
    /// </summary>
    Task DecrementConsumerJobCountAsync(string workerId, string jobType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Detects and marks zombie workers (no heartbeat for threshold time).
    /// </summary>
    Task<List<string>> DetectZombieWorkersAsync(TimeSpan threshold, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes worker from Redis (cleanup on graceful shutdown).
    /// </summary>
    Task<bool> RemoveWorkerAsync(string workerId, CancellationToken cancellationToken = default);
}
