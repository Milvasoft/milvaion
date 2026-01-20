namespace Milvaion.Infrastructure.Services.Redis.Utils;

/// <summary>
/// Circuit breaker statistics.
/// </summary>
public record CircuitBreakerStats
{
    /// <summary>
    /// Current circuit state
    /// </summary>
    public CircuitState State { get; init; }

    /// <summary>
    /// Consecutive failure count
    /// </summary>
    public int FailureCount { get; init; }

    /// <summary>
    /// Last failure time
    /// </summary>
    public DateTime? LastFailureTime { get; init; }

    /// <summary>
    /// Total operations count (resets hourly)
    /// </summary>
    public long TotalOperations { get; init; }

    /// <summary>
    /// Total failures count (resets hourly)
    /// </summary>
    public long TotalFailures { get; init; }

    /// <summary>
    /// When stats were last reset
    /// </summary>
    public DateTime StatsResetTime { get; init; }

    /// <summary>
    /// Success rate (0-1)
    /// </summary>
    public double SuccessRate => TotalOperations > 0 ? (TotalOperations - TotalFailures) / (double)TotalOperations : 0;
}