using Microsoft.Extensions.Diagnostics.HealthChecks;
using Milvaion.Infrastructure.Services.Redis;

namespace Milvaion.Infrastructure.HealthChecks;

/// <summary>
/// Health check for Redis connection.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="RedisHealthCheck"/> class.
/// </remarks>
public class RedisHealthCheck(RedisConnectionService redisConnection) : IHealthCheck
{
    private readonly RedisConnectionService _redisConnection = redisConnection;

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var isHealthy = await _redisConnection.IsHealthyAsync(cancellationToken);

            if (isHealthy)
            {
                return HealthCheckResult.Healthy("Redis connection is healthy", new Dictionary<string, object>
                {
                    ["ConnectionStatus"] = "Connected",
                    ["Database"] = _redisConnection.Database.Database
                });
            }

            return HealthCheckResult.Unhealthy("Redis connection is not available", null, new Dictionary<string, object>
            {
                ["ConnectionStatus"] = "Disconnected"
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
