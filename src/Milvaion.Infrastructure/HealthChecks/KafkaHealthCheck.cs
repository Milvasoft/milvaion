using Microsoft.Extensions.Diagnostics.HealthChecks;
using Milvaion.Infrastructure.Services.Kafka;

namespace Milvaion.Infrastructure.HealthChecks;

/// <summary>
/// Health check for the Kafka cluster: healthy when at least one broker answers a metadata request.
/// </summary>
public class KafkaHealthCheck(KafkaClientFactory clientFactory) : IHealthCheck
{
    private readonly KafkaClientFactory _clientFactory = clientFactory;

    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var metadata = _clientFactory.GetMetadata();

            if (metadata.Brokers.Count == 0)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy("No Kafka broker is available", null, new Dictionary<string, object>
                {
                    ["ConnectionStatus"] = "Disconnected",
                    ["BootstrapServers"] = _clientFactory.Options.BootstrapServers
                }));
            }

            return Task.FromResult(HealthCheckResult.Healthy("Kafka cluster is reachable", new Dictionary<string, object>
            {
                ["ConnectionStatus"] = "Connected",
                ["BootstrapServers"] = _clientFactory.Options.BootstrapServers,
                ["Brokers"] = metadata.Brokers.Count,
                ["ClusterOriginBroker"] = metadata.OriginatingBrokerName
            }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Kafka health check failed", ex, new Dictionary<string, object>
            {
                ["Error"] = ex.Message,
                ["BootstrapServers"] = _clientFactory.Options.BootstrapServers
            }));
        }
    }
}
