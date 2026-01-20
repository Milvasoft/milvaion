namespace Milvaion.Application.Dtos.HealthDtos;

/// <summary>
/// Health check response model.
/// </summary>
public class HealthCheckResponse
{
    /// <summary>
    /// Overall health status (Healthy, Degraded, Unhealthy).
    /// </summary>
    public string Status { get; set; }

    /// <summary>
    /// Total duration of all health checks.
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Timestamp when health check was performed.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Individual health check results.
    /// </summary>
    public List<HealthCheckEntry> Checks { get; set; }
}

/// <summary>
/// Individual health check entry.
/// </summary>
public class HealthCheckEntry
{
    /// <summary>
    /// Health check name (e.g., "Redis", "RabbitMQ").
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Health check status.
    /// </summary>
    public string Status { get; set; }

    /// <summary>
    /// Health check description.
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// Duration of this health check.
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Tags associated with this health check.
    /// </summary>
    public string[] Tags { get; set; }

    /// <summary>
    /// Additional data from health check.
    /// </summary>
    public Dictionary<string, string> Data { get; set; }
}

/// <summary>
/// Liveness response model.
/// </summary>
public class LivenessResponse
{
    /// <summary>
    /// Liveness status (always "Healthy" if responding).
    /// </summary>
    public string Status { get; set; }

    /// <summary>
    /// Timestamp when liveness check was performed.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Application uptime.
    /// </summary>
    public TimeSpan Uptime { get; set; }
}