using Microsoft.Extensions.Diagnostics.HealthChecks;
using Milvasoft.Milvaion.Sdk.Worker.Persistence;

namespace Milvasoft.Milvaion.Sdk.Worker.HealthChecks;

/// <summary>
/// Health check for RabbitMQ connection.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="RabbitMQHealthCheck"/> class.
/// </remarks>
/// <param name="connectionMonitor">The connection monitor for checking RabbitMQ health.</param>
public class RabbitMQHealthCheck(IConnectionMonitor connectionMonitor) : IHealthCheck
{
    private readonly IConnectionMonitor _connectionMonitor = connectionMonitor;

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var isHealthy = await _connectionMonitor.RefreshStatusAsync();

            if (isHealthy)
            {
                return HealthCheckResult.Healthy("RabbitMQ connection is healthy", new Dictionary<string, object>
                {
                    ["ConnectionStatus"] = "Connected"
                });
            }

            return HealthCheckResult.Unhealthy("RabbitMQ connection is not available", null, new Dictionary<string, object>
            {
                ["ConnectionStatus"] = "Disconnected"
            });
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("RabbitMQ health check failed", ex, new Dictionary<string, object>
            {
                ["Error"] = ex.Message
            });
        }
    }
}
