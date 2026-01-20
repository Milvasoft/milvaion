using Microsoft.Extensions.Diagnostics.HealthChecks;
using Milvaion.Infrastructure.Services.RabbitMQ;

namespace Milvaion.Infrastructure.HealthChecks;

/// <summary>
/// Health check for RabbitMQ connection.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="RabbitMQHealthCheck"/> class.
/// </remarks>
public class RabbitMQHealthCheck(RabbitMQConnectionFactory rabbitMQConnection) : IHealthCheck
{
    private readonly RabbitMQConnectionFactory _rabbitMQConnection = rabbitMQConnection;

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var isHealthy = _rabbitMQConnection.IsHealthy();

            if (isHealthy)
            {
                var connection = _rabbitMQConnection.Connection;
                var endpoint = connection.Endpoint;

                return HealthCheckResult.Healthy("RabbitMQ connection is healthy", new Dictionary<string, object>
                {
                    ["ConnectionStatus"] = "Connected",
                    ["Host"] = endpoint.HostName,
                    ["Port"] = endpoint.Port,
                    ["IsOpen"] = connection.IsOpen
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
