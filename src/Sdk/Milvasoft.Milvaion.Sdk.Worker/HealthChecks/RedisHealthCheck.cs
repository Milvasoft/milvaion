using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Milvasoft.Milvaion.Sdk.Worker.HealthChecks;

/// <summary>
/// Health check for Redis connection.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="RedisHealthCheck"/> class.
/// </remarks>
/// <param name="redis">The Redis connection multiplexer.</param>
public class RedisHealthCheck(IConnectionMultiplexer redis) : IHealthCheck
{
    private readonly IConnectionMultiplexer _redis = redis;

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (_redis == null || !_redis.IsConnected)
            {
                return HealthCheckResult.Unhealthy("Redis connection is not available", null, new Dictionary<string, object>
                {
                    ["ConnectionStatus"] = "Disconnected"
                });
            }

            var latency = await _redis.GetDatabase().PingAsync();

            return HealthCheckResult.Healthy("Redis connection is healthy", new Dictionary<string, object>
            {
                ["ConnectionStatus"] = "Connected",
                ["LatencyMs"] = latency.TotalMilliseconds,
                ["IsConnected"] = _redis.IsConnected
            });
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Redis health check failed", ex, new Dictionary<string, object>
            {
                ["Error"] = ex.Message
            });
        }
    }
}
