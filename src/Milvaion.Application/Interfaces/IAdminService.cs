using Milvaion.Application.Dtos.AdminDtos;
using Milvasoft.Components.Rest.MilvaResponse;

namespace Milvaion.Application.Interfaces;

/// <summary>
/// Implementation of admin service.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="IAdminService"/> class.
/// </remarks>
public interface IAdminService
{
    /// <summary>
    /// Gets queue statistics for all queues.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Queue statistics</returns>
    public Task<Response<List<QueueStats>>> GetQueueStatsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets detailed information about a specific queue.
    /// </summary>
    /// <param name="queueName">Queue name</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Queue depth information</returns>
    public Task<Response<QueueDepthInfo>> GetQueueInfoAsync(string queueName, CancellationToken cancellationToken);

    /// <summary>
    /// Gets system health overview including dispatcher status.
    /// </summary>
    /// <returns>System health information</returns>
    public Task<Response<SystemHealthInfo>> GetSystemHealthAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Emergency stop - Disables the job dispatcher at runtime.
    /// </summary>
    /// <param name="reason">Reason for emergency stop</param>
    /// <returns>Success response</returns>
    public IResponse EmergencyStop(string reason);

    /// <summary>
    /// Resume operations - Enables the job dispatcher at runtime.
    /// </summary>
    /// <returns>Success response</returns>
    public IResponse ResumeOperations();

    /// <summary>
    /// Gets job statistics grouped by status.
    /// </summary>
    /// <returns>Job statistics</returns>
    public Task<Response<JobStatistics>> GetJobStatisticsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets Redis circuit breaker statistics.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Circuit breaker statistics</returns>
    public Response<RedisCircuitBreakerStatsDto> GetRedisCircuitBreakerStats(CancellationToken cancellationToken);

    /// <summary>
    /// Gets database statistics including table sizes, occurrence growth, and large occurrences.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Database statistics</returns>
    public Task<Response<DatabaseStatisticsDto>> GetDatabaseStatisticsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets background service memory diagnostics.
    /// </summary>
    /// <returns>Database statistics</returns>
    public Response<AggregatedMemoryStats> GetBackgroundServiceMemoryDiagnostics();
}
