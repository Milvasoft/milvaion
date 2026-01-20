namespace Milvaion.Application.Dtos.AdminDtos;

/// <summary>
/// Redis circuit breaker statistics DTO
/// </summary>
public class RedisCircuitBreakerStatsDto
{
    /// <summary>
    /// Current circuit state
    /// </summary>
    public string State { get; set; }

    /// <summary>
    /// Consecutive failure count
    /// </summary>
    public int FailureCount { get; set; }

    /// <summary>
    /// Last failure time
    /// </summary>
    public DateTime? LastFailureTime { get; set; }

    /// <summary>
    /// Total operations count
    /// </summary>
    public long TotalOperations { get; set; }

    /// <summary>
    /// Total failures count
    /// </summary>
    public long TotalFailures { get; set; }

    /// <summary>
    /// Success rate percentage (0-100)
    /// </summary>
    public double SuccessRatePercentage { get; set; }

    /// <summary>
    /// Health status based on circuit state
    /// </summary>
    public string HealthStatus { get; set; }

    /// <summary>
    /// Human-readable health message
    /// </summary>
    public string HealthMessage { get; set; }

    /// <summary>
    /// Time since last failure (if any)
    /// </summary>
    public string TimeSinceLastFailure { get; set; }

    /// <summary>
    /// Recommendation for action
    /// </summary>
    public string Recommendation { get; set; }
}
