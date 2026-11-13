using Microsoft.Extensions.Diagnostics.HealthChecks;
using Milvasoft.Milvaion.Sdk.Worker.Persistence;

namespace Milvasoft.Milvaion.Sdk.Worker.HealthChecks;

/// <summary>
/// Health check for the Kafka cluster, for workers that run on Kafka.
/// </summary>
/// <param name="connectionMonitor">The connection monitor, which tracks the broker of the configured transport.</param>
public class KafkaHealthCheck(IConnectionMonitor connectionMonitor) : IHealthCheck
{
    private readonly IConnectionMonitor _connectionMonitor = connectionMonitor;

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await _connectionMonitor.RefreshStatusAsync();

            // On Kafka the monitor's broker state is the Kafka cluster's.
            if (_connectionMonitor.IsRabbitMQHealthy)
            {
                return HealthCheckResult.Healthy("Kafka cluster is reachable", new Dictionary<string, object>
                {
                    ["ConnectionStatus"] = "Connected"
                });
            }

            return HealthCheckResult.Unhealthy("Kafka cluster is not available", null, new Dictionary<string, object>
            {
                ["ConnectionStatus"] = "Disconnected"
            });
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Kafka health check failed", ex, new Dictionary<string, object>
            {
                ["Error"] = ex.Message
            });
        }
    }
}
