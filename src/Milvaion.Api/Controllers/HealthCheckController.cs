using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Milvaion.Application.Dtos.HealthDtos;
using Milvaion.Application.Utils.Constants;
using System.Net;

namespace Milvaion.Api.Controllers;

/// <summary>
/// Health check endpoints for monitoring and orchestration.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="HealthCheckController"/> class.
/// </remarks>
[ApiController]
[Route(GlobalConstant.FullRoute)]
[ApiVersion(GlobalConstant.CurrentApiVersion)]
[ApiExplorerSettings(GroupName = "v1.0")]
[AllowAnonymous]
public class HealthCheckController(HealthCheckService healthCheckService) : ControllerBase
{
    private readonly HealthCheckService _healthCheckService = healthCheckService;

    /// <summary>
    /// Basic health check - returns OK if API is running.
    /// </summary>
    /// <returns>OK response</returns>
    [HttpGet]
    [ProducesResponseType<string>((int)HttpStatusCode.OK)]
    public IActionResult HealthCheck() => Ok("Ok");

    /// <summary>
    /// Readiness probe - checks if application is ready to serve traffic.
    /// Validates all dependencies (Redis, RabbitMQ, Database).
    /// Use this for Kubernetes readiness probes.
    /// </summary>
    /// <returns>Health check result with all dependency statuses</returns>
    /// <response code="200">Application is ready and all dependencies are healthy</response>
    /// <response code="503">Application is not ready - one or more dependencies are unhealthy</response>
    [HttpGet("ready")]
    [ProducesResponseType<HealthCheckResponse>((int)HttpStatusCode.OK)]
    [ProducesResponseType<HealthCheckResponse>((int)HttpStatusCode.ServiceUnavailable)]
    public async Task<IActionResult> ReadinessProbe(CancellationToken cancellationToken)
    {
        var healthReport = await _healthCheckService.CheckHealthAsync(cancellationToken);

        var response = new HealthCheckResponse
        {
            Status = healthReport.Status.ToString(),
            Duration = healthReport.TotalDuration,
            Timestamp = DateTime.UtcNow,
            Checks = [.. healthReport.Entries.Select(e => new HealthCheckEntry
            {
                Name = e.Key,
                Status = e.Value.Status.ToString(),
                Description = e.Value.Description,
                Duration = e.Value.Duration,
                Tags = [.. e.Value.Tags],
                Data = e.Value.Data.ToDictionary(d => d.Key, d => d.Value?.ToString())
            })]
        };

        return healthReport.Status == HealthStatus.Healthy ? Ok(response) : StatusCode((int)HttpStatusCode.ServiceUnavailable, response);
    }

    /// <summary>
    /// Liveness probe - checks if application is alive and responsive.
    /// Use this for Kubernetes liveness probes.
    /// Returns 200 OK if the application process is running.
    /// </summary>
    /// <returns>Liveness status</returns>
    /// <response code="200">Application is alive and responsive</response>
    [HttpGet("live")]
    [ProducesResponseType<LivenessResponse>((int)HttpStatusCode.OK)]
    public IActionResult LivenessProbe() => Ok(new LivenessResponse
    {
        Status = "Healthy",
        Timestamp = DateTime.UtcNow,
        Uptime = GetUptime()
    });

    /// <summary>
    /// Startup probe - checks if application has completed startup.
    /// Use this for Kubernetes startup probes (slower startups).
    /// </summary>
    /// <returns>Startup completion status</returns>
    /// <response code="200">Application startup completed successfully</response>
    [HttpGet("startup")]
    [ProducesResponseType<LivenessResponse>((int)HttpStatusCode.OK)]
    public IActionResult StartupProbe() => Ok(new LivenessResponse
    {
        Status = "Started",
        Timestamp = DateTime.UtcNow,
        Uptime = GetUptime()
    });

    /// <summary>
    /// Gets application uptime.
    /// </summary>
    private static TimeSpan GetUptime()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();

        return DateTime.UtcNow - process.StartTime.ToUniversalTime();
    }
}
