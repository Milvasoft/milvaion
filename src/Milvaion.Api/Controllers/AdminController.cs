using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Milvaion.Application.Dtos.AdminDtos;
using Milvaion.Application.Dtos.ConfigurationDtos;
using Milvaion.Application.Features.Configuration.GetSystemConfiguration;
using Milvaion.Application.Features.Configuration.GetSystemResourceUsage;
using Milvaion.Application.Utils.Attributes;
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
[Auth]
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
    /// Gets top tables by size.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Table sizes</returns>
    [Auth(PermissionCatalog.SystemAdministration.List)]
    [HttpGet("database-statistics/tables")]
    [ProducesResponseType(typeof(Response<List<TableSizeDto>>), StatusCodes.Status200OK)]
    public Task<Response<List<TableSizeDto>>> GetTableSizesAsync(CancellationToken cancellationToken) => _adminService.GetTableSizesAsync(cancellationToken);

    /// <summary>
    /// Gets index efficiency statistics (unused/underutilized indexes).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Index efficiency statistics</returns>
    [Auth(PermissionCatalog.SystemAdministration.List)]
    [HttpGet("database-statistics/indexes")]
    [ProducesResponseType(typeof(Response<IndexEfficiencyDto>), StatusCodes.Status200OK)]
    public Task<Response<IndexEfficiencyDto>> GetIndexEfficiencyAsync(CancellationToken cancellationToken) => _adminService.GetIndexEfficiencyAsync(cancellationToken);

    /// <summary>
    /// Gets database cache hit ratio.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Cache hit ratio statistics</returns>
    [Auth(PermissionCatalog.SystemAdministration.List)]
    [HttpGet("database-statistics/cache")]
    [ProducesResponseType(typeof(Response<CacheHitRatioDto>), StatusCodes.Status200OK)]
    public Task<Response<CacheHitRatioDto>> GetCacheHitRatioAsync(CancellationToken cancellationToken) => _adminService.GetCacheHitRatioAsync(cancellationToken);

    /// <summary>
    /// Gets table bloat detection (VACUUM recommendation).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Table bloat statistics</returns>
    [Auth(PermissionCatalog.SystemAdministration.List)]
    [HttpGet("database-statistics/bloat")]
    [ProducesResponseType(typeof(Response<TableBloatDto>), StatusCodes.Status200OK)]
    public Task<Response<TableBloatDto>> GetTableBloatAsync(CancellationToken cancellationToken) => _adminService.GetTableBloatAsync(cancellationToken);

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

    /// <summary>
    /// Gets live CPU, memory and disk usage of the API host and process.
    /// </summary>
    /// <param name="request">Query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resource usage sample.</returns>
    /// <remarks>
    /// Deliberately narrow. <c>configuration</c> returns these numbers too, but only as one branch of a payload
    /// that also walks every configuration section, parses the connection strings and enumerates the alerting
    /// channels - too expensive to call when the question is just how much memory the process is using. This
    /// reads the current process and nothing else, so it is cheap enough to poll.
    /// </remarks>
    [Auth(PermissionCatalog.SystemAdministration.List)]
    [HttpGet("resource-usage")]
    [ProducesResponseType(typeof(Response<SystemResourceUsageDto>), StatusCodes.Status200OK)]
    public Task<Response<SystemResourceUsageDto>> GetResourceUsageAsync([FromQuery] GetSystemResourceUsageQuery request, CancellationToken cancellationToken)
        => _mediator.Send(request, cancellationToken);
}
