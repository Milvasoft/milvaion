using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Milvaion.Application.Dtos.MetricReportDtos;
using Milvaion.Application.Features.MetricReports.DeleteMetricReport;
using Milvaion.Application.Features.MetricReports.DeleteOldMetricReports;
using Milvaion.Application.Features.MetricReports.GetLatestMetricReport;
using Milvaion.Application.Features.MetricReports.GetMetricReportDetail;
using Milvaion.Application.Features.MetricReports.GetMetricReportList;
using Milvaion.Application.Utils.Attributes;
using Milvasoft.Components.Rest.MilvaResponse;

namespace Milvaion.Api.Controllers;

/// <summary>
/// Manages metric report operations including listing, retrieving, and deleting generated performance reports.
/// Reports are produced by ReporterWorker and stored in MilvaionDb.
/// </summary>
[ApiController]
[Route(GlobalConstant.FullRoute)]
[ApiVersion(GlobalConstant.CurrentApiVersion)]
[ApiExplorerSettings(GroupName = "v1.0")]
[Auth]
public class MetricReportsController(IMediator mediator) : ControllerBase
{
    private readonly IMediator _mediator = mediator;

    /// <summary>
    /// Returns a paginated and filterable list of metric reports. Supports sorting and optional filtering by metric type.
    /// </summary>
    /// <param name="request">Pagination, sorting, and optional MetricType filter parameters.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Paginated list of <see cref="MetricReportListDto"/>.</returns>
    [Auth(PermissionCatalog.ScheduledJobManagement.List)]
    [HttpPatch]
    public Task<ListResponse<MetricReportListDto>> GetReportsAsync(GetMetricReportListQuery request, CancellationToken cancellationToken)
        => _mediator.Send(request, cancellationToken);

    /// <summary>
    /// Returns full details of a specific metric report by its unique identifier.
    /// </summary>
    /// <param name="request">Contains the report Id.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Detailed <see cref="MetricReportDetailDto"/> including the JSON report data.</returns>
    [Auth(PermissionCatalog.ScheduledJobManagement.Detail)]
    [HttpGet]
    public Task<Response<MetricReportDetailDto>> GetReportByIdAsync([FromQuery] GetMetricReportDetailQuery request, CancellationToken cancellationToken)
        => _mediator.Send(request, cancellationToken);

    /// <summary>
    /// Returns the most recently generated report for the specified metric type
    /// (e.g. FailureRateTrend, WorkerThroughput, JobHealthScore).
    /// </summary>
    /// <param name="request">Contains the MetricType to look up.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Latest <see cref="MetricReportDetailDto"/> for the given type, or an error if none exists.</returns>
    [Auth(PermissionCatalog.ScheduledJobManagement.Detail)]
    [HttpGet("latest")]
    public Task<Response<MetricReportDetailDto>> GetLatestReportByTypeAsync([FromQuery] GetLatestMetricReportQuery request, CancellationToken cancellationToken)
        => _mediator.Send(request, cancellationToken);

    /// <summary>
    /// Permanently deletes a single metric report by its unique identifier.
    /// </summary>
    /// <param name="request">Contains the report Id to delete.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>The Id of the deleted report.</returns>
    [Auth(PermissionCatalog.ScheduledJobManagement.Delete)]
    [HttpDelete]
    public Task<Response<Guid>> DeleteReportAsync([FromQuery] DeleteMetricReportCommand request, CancellationToken cancellationToken)
        => _mediator.Send(request, cancellationToken);

    /// <summary>
    /// Bulk-deletes metric reports older than the specified number of days.
    /// Intended for periodic data retention cleanup.
    /// </summary>
    /// <param name="request">Contains the olderThanDays threshold.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>The number of reports deleted.</returns>
    [Auth(PermissionCatalog.ScheduledJobManagement.Delete)]
    [HttpDelete("cleanup")]
    public Task<Response<int>> DeleteOldReportsAsync([FromQuery] DeleteOldMetricReportsCommand request, CancellationToken cancellationToken)
        => _mediator.Send(request, cancellationToken);
}

