namespace Milvaion.Infrastructure.Services.Redis.Utils;

/// <summary>
/// Exception thrown when circuit breaker is open.
/// </summary>
public class RedisCircuitBreakerOpenException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RedisCircuitBreakerOpenException"/> class.
    /// </summary>
    public RedisCircuitBreakerOpenException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisCircuitBreakerOpenException"/> class.
    /// </summary>
    public RedisCircuitBreakerOpenException(string message, Exception innerException) : base(message, innerException)
    {
    }
}