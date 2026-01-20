namespace Milvaion.Infrastructure.Services.Redis.Utils;

/// <summary>
/// Circuit breaker state.
/// </summary>
public enum CircuitState
{
    /// <summary>
    /// Circuit is closed, operations flow normally
    /// </summary>
    Closed,

    /// <summary>
    /// Circuit is open, operations are blocked
    /// </summary>
    Open,

    /// <summary>
    /// Circuit is half-open, testing if service recovered
    /// </summary>
    HalfOpen
}