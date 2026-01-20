namespace Milvaion.Application.Interfaces.Redis;

/// <summary>
/// Distributed lock service using Redis for preventing duplicate job execution.
/// </summary>
public interface IRedisLockService
{
    /// <summary>
    /// Tries to acquire a lock for a job.
    /// </summary>
    /// <param name="jobId">Job identifier</param>
    /// <param name="workerId">Worker identifier (machine name, container ID, etc.)</param>
    /// <param name="ttl">Lock time-to-live duration</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if lock acquired, false if already locked</returns>
    Task<bool> TryAcquireLockAsync(Guid jobId, string workerId, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a lock for a job.
    /// </summary>
    /// <param name="jobId">Job identifier</param>
    /// <param name="workerId">Worker identifier (must match the one who acquired the lock)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if released, false if lock not found or owned by another worker</returns>
    Task<bool> ReleaseLockAsync(Guid jobId, string workerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a job is currently locked.
    /// </summary>
    /// <param name="jobId">Job identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if locked, false otherwise</returns>
    Task<bool> IsLockedAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the worker ID that currently holds the lock.
    /// </summary>
    /// <param name="jobId">Job identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Worker ID or null if not locked</returns>
    Task<string> GetLockOwnerAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extends the TTL of an existing lock.
    /// </summary>
    /// <param name="jobId">Job identifier</param>
    /// <param name="workerId">Worker identifier (must match the one who acquired the lock)</param>
    /// <param name="ttl">New TTL duration</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if extended, false if lock not found or owned by another worker</returns>
    Task<bool> ExtendLockAsync(Guid jobId, string workerId, TimeSpan ttl, CancellationToken cancellationToken = default);
}
