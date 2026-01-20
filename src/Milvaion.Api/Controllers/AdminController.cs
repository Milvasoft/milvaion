using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Milvaion.Application.Dtos.AdminDtos;
using Milvaion.Application.Dtos.ConfigurationDtos;
using Milvaion.Application.Features.Configuration.GetSystemConfiguration;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.Attributes;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.PermissionManager;
using Milvaion.Domain.Enums;
using Milvasoft.Components.Rest.MilvaResponse;

namespace Milvaion.Api.Controllers;

/// <summary>
/// Admin controls for system monitoring and emergency operations.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="AdminController"/> class.
/// </remarks>
[ApiController]
[Route(GlobalConstant.FullRoute)]
[ApiVersion(GlobalConstant.CurrentApiVersion)]
[ApiExplorerSettings(GroupName = "v1.0")]
[UserTypeAuth(UserType.Manager)]
public class AdminController(IAdminService adminService,
                             IMediator mediator) : ControllerBase
{
    private readonly IMediator _mediator = mediator;
    private readonly IAdminService _adminService = adminService;

    /// <summary>
    /// Gets queue statistics for all queues.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Queue statistics</returns>
    [Auth(PermissionCatalog.SystemAdministration.List)]
    [HttpGet("queue-stats")]
    [ProducesResponseType(typeof(Response<List<QueueStats>>), StatusCodes.Status200OK)]
    public Task<Response<List<QueueStats>>> GetQueueStatsAsync(CancellationToken cancellationToken) => _adminService.GetQueueStatsAsync(cancellationToken);

    /// <summary>
    /// Gets detailed information about a specific queue.
    /// </summary>
    /// <param name="queueName">Queue name</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Queue depth information</returns>
    [Auth(PermissionCatalog.SystemAdministration.List)]
    [HttpGet("queue/{queueName}")]
    [ProducesResponseType(typeof(Response<QueueDepthInfo>), StatusCodes.Status200OK)]
    public Task<Response<QueueDepthInfo>> GetQueueInfoAsync([FromRoute] string queueName, CancellationToken cancellationToken) => _adminService.GetQueueInfoAsync(queueName, cancellationToken);

    /// <summary>
    /// Gets system health overview including dispatcher status.
    /// </summary>
    /// <returns>System health information</returns>
    [Auth(PermissionCatalog.SystemAdministration.List)]
    [HttpGet("system-health")]
    [ProducesResponseType(typeof(Response<SystemHealthInfo>), StatusCodes.Status200OK)]
    public Task<Response<SystemHealthInfo>> GetSystemHealthAsync(CancellationToken cancellationToken) => _adminService.GetSystemHealthAsync(cancellationToken);

    /// <summary>
    /// Gets system configuration (read-only).
    /// </summary>
    /// <param name="request">Query parameters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>System configuration</returns>
    [Auth(PermissionCatalog.SystemAdministration.List)]
    [HttpGet("configuration")]
    [ProducesResponseType(typeof(Response<SystemConfigurationDto>), StatusCodes.Status200OK)]
    public Task<Response<SystemConfigurationDto>> GetConfigurationAsync([FromQuery] GetSystemConfigurationQuery request, CancellationToken cancellationToken) => _mediator.Send(request, cancellationToken);

    /// <summary>
    /// Gets job statistics grouped by status.
    /// </summary>
    /// <returns>Job statistics</returns>
    [Auth(PermissionCatalog.SystemAdministration.List)]
    [HttpGet("job-stats")]
    [ProducesResponseType(typeof(Response<JobStatistics>), StatusCodes.Status200OK)]
    public Task<Response<JobStatistics>> GetJobStatisticsAsync(CancellationToken cancellationToken) => _adminService.GetJobStatisticsAsync(cancellationToken);

    /// <summary>
    /// Gets Redis circuit breaker statistics and health status.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Circuit breaker statistics</returns>
    [Auth(PermissionCatalog.SystemAdministration.List)]
    [HttpGet("redis-circuit-breaker")]
    [ProducesResponseType(typeof(Response<RedisCircuitBreakerStatsDto>), StatusCodes.Status200OK)]
    public Response<RedisCircuitBreakerStatsDto> GetRedisCircuitBreakerStats(CancellationToken cancellationToken) => _adminService.GetRedisCircuitBreakerStats(cancellationToken);

    /// <summary>
    /// Gets database statistics including table sizes, occurrence growth, and large occurrences.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Database statistics</returns>
    [Auth(PermissionCatalog.SystemAdministration.List)]
    [HttpGet("database-statistics")]
    [ProducesResponseType(typeof(Response<DatabaseStatisticsDto>), StatusCodes.Status200OK)]
    public Task<Response<DatabaseStatisticsDto>> GetDatabaseStatisticsAsync(CancellationToken cancellationToken) => _adminService.GetDatabaseStatisticsAsync(cancellationToken);

    /// <summary>
    /// Emergency stop. Disables the job dispatcher at runtime.
    /// </summary>
    /// <param name="reason">Reason for emergency stop</param>
    /// <returns>Success response</returns>
    [Auth(PermissionCatalog.SystemAdministration.Update)]
    [HttpPost("jobdispatcher/stop")]
    [ProducesResponseType(typeof(IResponse), StatusCodes.Status200OK)]
    public IResponse EmergencyStop([FromQuery] string reason = "Manual emergency stop") => _adminService.EmergencyStop(reason);

    /// <summary>
    /// Resume job dispatcher operations. Enables the job dispatcher at runtime.
    /// </summary>
    /// <returns>Success response</returns>
    [Auth(PermissionCatalog.SystemAdministration.Update)]
    [HttpPost("jobdispatcher/resume")]
    [ProducesResponseType(typeof(IResponse), StatusCodes.Status200OK)]
    public IResponse ResumeOperations() => _adminService.ResumeOperations();

    /// <summary>
    /// Gets background services memory diagnostics.
    /// </summary>
    /// <returns>Success response</returns>
    [Auth(PermissionCatalog.SystemAdministration.List)]
    [HttpGet("diagnostics/services")]
    [ProducesResponseType(typeof(Response<AggregatedMemoryStats>), StatusCodes.Status200OK)]
    public Response<AggregatedMemoryStats> GetBackgrounServiceMemoryDiagnostics() => _adminService.GetBackgroundServiceMemoryDiagnostics();
}
