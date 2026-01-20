using Microsoft.Extensions.Logging;
using Milvaion.Infrastructure.Services.Redis.Utils;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Utils;
using StackExchange.Redis;

namespace Milvaion.Infrastructure.Services.Redis;

/// <summary>
/// Circuit breaker pattern implementation for Redis operations.
/// Prevents cascading failures when Redis is unavailable.
/// </summary>
public interface IRedisCircuitBreaker
{
    /// <summary>
    /// Executes a Redis operation with circuit breaker protection.
    /// </summary>
    /// <typeparam name="T">Return type</typeparam>
    /// <param name="operation">Redis operation to execute</param>
    /// <param name="fallback">Fallback function if circuit is open</param>
    /// <param name="operationName">Name for logging</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of operation or fallback</returns>
    Task<T> ExecuteAsync<T>(Func<Task<T>> operation,
                            Func<Task<T>> fallback = null,
                            string operationName = "RedisOperation",
                            CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current circuit state.
    /// </summary>
    CircuitState State { get; }

    /// <summary>
    /// Gets circuit breaker statistics.
    /// </summary>
    CircuitBreakerStats GetStats();
}

/// <summary>
/// Redis circuit breaker implementation.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="RedisCircuitBreaker"/> class.
/// </remarks>
public class RedisCircuitBreaker(ILoggerFactory loggerFactory) : IRedisCircuitBreaker
{
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<RedisCircuitBreaker>();
    private readonly Lock _lock = new();
    private CircuitState _state = CircuitState.Closed;
    private int _consecutiveFailures = 0;
    private DateTime? _lastFailureTime = null;
    private long _totalOperations = 0;
    private long _totalFailures = 0;
    private DateTime _statsResetTime = DateTime.UtcNow;
    private readonly int _failureThreshold = 5;
    private readonly TimeSpan _openTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _statsResetInterval = TimeSpan.FromHours(1); // Reset stats every hour

    /// <inheritdoc/>
    public CircuitState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    /// <inheritdoc/>
    public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation,
                                         Func<Task<T>> fallback = null,
                                         string operationName = "RedisOperation",
                                         CancellationToken cancellationToken = default)
    {
        bool shouldUseFallback;
        bool shouldTransitionToHalfOpen;

        lock (_lock)
        {
            // Reset stats periodically to prevent overflow
            if (DateTime.UtcNow - _statsResetTime > _statsResetInterval)
            {
                _logger.Information("Resetting circuit breaker stats. Previous: {TotalOperations} operations, {TotalFailures} failures, Success rate: {SuccessRate:P2}",
                    _totalOperations, _totalFailures, _totalOperations > 0 ? (_totalOperations - _totalFailures) / (double)_totalOperations : 0);

                _totalOperations = 0;
                _totalFailures = 0;
                _statsResetTime = DateTime.UtcNow;
            }

            _totalOperations++;

            // Check if circuit should transition from Open to HalfOpen
            shouldTransitionToHalfOpen = _state == CircuitState.Open && DateTime.UtcNow - _lastFailureTime > _openTimeout;

            shouldUseFallback = _state == CircuitState.Open && !shouldTransitionToHalfOpen;

            if (shouldTransitionToHalfOpen)
            {
                _logger.Information("Circuit breaker transitioning from Open to HalfOpen for {Operation}. Testing if Redis recovered.", operationName);
                _state = CircuitState.HalfOpen;
            }
        }

        // Use fallback outside lock if circuit is open
        if (shouldUseFallback)
        {
            _logger.Warning("Circuit breaker is OPEN for {Operation}. Using fallback. Time until retry: {TimeUntilRetry}s", operationName, (_openTimeout - (DateTime.UtcNow - _lastFailureTime.Value)).TotalSeconds);

            if (fallback != null)
                return await fallback();

            throw new RedisCircuitBreakerOpenException($"Circuit breaker is OPEN for {operationName}. Redis is unavailable.");
        }

        try
        {
            var result = await operation();

            // Success - reset failure counter
            lock (_lock)
            {
                if (_consecutiveFailures > 0)
                {
                    _logger.Information("Circuit breaker recovered for {Operation}. Resetting failure count (was: {FailureCount})", operationName, _consecutiveFailures);
                }

                _consecutiveFailures = 0;
                _state = CircuitState.Closed;
            }

            return result;
        }
        catch (RedisException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return await HandleFailureAsync(fallback, operationName, ex, cancellationToken);
        }
        catch (RedisConnectionException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return await HandleFailureAsync(fallback, operationName, ex, cancellationToken);
        }
        catch (TimeoutException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return await HandleFailureAsync(fallback, operationName, ex, cancellationToken);
        }
    }

    private Task<T> HandleFailureAsync<T>(Func<Task<T>> fallback,
                                          string operationName,
                                          Exception exception,
                                          CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _consecutiveFailures++;
            _totalFailures++;
            _lastFailureTime = DateTime.UtcNow;

            _logger.Error(exception, "Redis operation failed: {Operation}, Consecutive failures: {Failures}/{Threshold}", operationName, _consecutiveFailures, _failureThreshold);

            if (_consecutiveFailures >= _failureThreshold && _state != CircuitState.Open)
            {
                _state = CircuitState.Open;
                _logger.Fatal("Circuit breaker OPENED for Redis operations after {Failures} consecutive failures. Circuit will remain open for {Timeout}s before attempting recovery.", _consecutiveFailures, _openTimeout.TotalSeconds);
            }
        }

        if (fallback != null)
        {
            _logger.Information("Using fallback for {Operation}", operationName);

            // Check cancellation before executing fallback
            cancellationToken.ThrowIfCancellationRequested();

            return fallback();
        }

        throw new RedisCircuitBreakerOpenException($"Redis operation '{operationName}' failed: {exception.Message}", exception);
    }

    /// <inheritdoc/>
    public CircuitBreakerStats GetStats()
    {
        lock (_lock)
        {
            return new CircuitBreakerStats
            {
                State = _state,
                FailureCount = _consecutiveFailures,
                LastFailureTime = _lastFailureTime,
                TotalOperations = _totalOperations,
                TotalFailures = _totalFailures,
                StatsResetTime = _statsResetTime
            };
        }
    }
}