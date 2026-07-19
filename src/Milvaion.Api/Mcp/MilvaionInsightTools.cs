using MediatR;
using Milvaion.Application.Dtos.MetricReportDtos;
using Milvaion.Application.Features.Configuration.GetSystemConfiguration;
using Milvaion.Application.Features.InternalNotifications.GetInternalNotificationList;
using Milvaion.Application.Features.MetricReports.GetLatestMetricReport;
using Milvaion.Application.Features.MetricReports.GetMetricReportDetail;
using Milvaion.Application.Features.MetricReports.GetMetricReportSummaryList;
using Milvaion.Application.Features.Permissions.GetPermissionList;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace Milvaion.Api.Mcp;

/// <summary>
/// MCP tools for reports, infrastructure health and configuration.
/// </summary>
/// <remarks>
/// Everything here is read-only. These answer the questions that sit one level above an individual job - is the
/// queue backing up, is the database growing, what did the scheduler decide overnight - and they are usually
/// what turns "this job failed" into "the whole box is unhealthy".
/// </remarks>
[McpServerToolType]
public class MilvaionInsightTools(IMediator mediator, IAdminService adminService, McpPermissionGuard guard)
{
    private readonly IMediator _mediator = mediator;
    private readonly IAdminService _adminService = adminService;
    private readonly McpPermissionGuard _guard = guard;

    private const int _maxPageSize = 100;

    #region Metric reports

    /// <summary>
    /// Gets generated metric reports.
    /// </summary>
    /// <param name="metricType">Only reports of this type.</param>
    /// <param name="pageNumber">Page number, starting at 1.</param>
    /// <param name="pageSize">Results per page, capped at <see cref="_maxPageSize"/>.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Paged report list with the total count.</returns>
    /// <remarks>
    /// Uses the summary query rather than the list query the dashboard uses. The list DTO carries each report's
    /// full jsonb payload, so a single page of throughput reports is hundreds of kilobytes of aggregated series -
    /// paid for on every call, and thrown away whenever the caller only wanted to know which reports exist.
    /// </remarks>
    [McpServerTool(Name = "list_reports", ReadOnly = true)]
    [Description("Lists metric reports produced by the reporter worker - failure rate trends, percentile durations, slowest jobs, worker throughput and utilisation. Returns metadata only, so use it to discover what exists and how fresh it is, then fetch the numbers with get_latest_report or get_report. ageMinutes is worth checking before quoting a report: the reporter worker runs on a schedule, so the newest report of a type can still be hours old.")]
    public async Task<object> ListReportsAsync(
        [Description("Only reports of this metric type. Call once without it to discover the available types.")] string metricType = null,
        [Description("Page number, starting at 1.")] int pageNumber = 1,
        [Description("Results per page. Maximum 100.")] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.ScheduledJobManagement.List);

        var response = await _mediator.Send(new GetMetricReportSummaryListQuery
        {
            MetricType = metricType,
            PageNumber = pageNumber < 1 ? 1 : pageNumber,
            RowCount = Math.Clamp(pageSize, 1, _maxPageSize),
            Sorting = new SortRequest { SortBy = nameof(MetricReport.Id), Type = SortType.Desc }
        }, cancellationToken);

        return new
        {
            totalCount = response.TotalDataCount,
            pageNumber,
            reports = response.Data,
            note = "Report payloads are omitted here. Call get_report with an id, or get_latest_report with a metric type, to read the numbers."
        };
    }

    /// <summary>
    /// Gets the most recent report of a given type.
    /// </summary>
    /// <param name="metricType">Metric type to fetch the latest report for.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>The latest report of that type.</returns>
    /// <exception cref="McpException">Thrown when no report of that type exists yet.</exception>
    [McpServerTool(Name = "get_latest_report", ReadOnly = true)]
    [Description("Gets the most recently generated report of a given metric type, with its full data. This is the fastest way to answer questions about trends - failure rates, slow jobs, worker throughput - because the reporter worker has already done the aggregation. Call list_reports first if you do not know which metric types exist.")]
    public async Task<object> GetLatestReportAsync(
        [Description("The metric type, as seen in list_reports.")] string metricType,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.ScheduledJobManagement.Detail);

        var response = await _mediator.Send(new GetLatestMetricReportQuery { MetricType = metricType }, cancellationToken);

        if (response.Data == null)
            throw new McpException($"No report has been generated yet for metric type '{metricType}'. Call list_reports to see which types exist.");

        return Unwrap(response.Data);
    }

    /// <summary>
    /// Gets one report by id.
    /// </summary>
    /// <param name="reportId">Report id.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Report detail.</returns>
    /// <exception cref="McpException">Thrown when no report exists with the given id.</exception>
    [McpServerTool(Name = "get_report", ReadOnly = true)]
    [Description("Gets one metric report in full by its id, as returned by list_reports. Use this to compare an older report against the current one and see how a trend moved.")]
    public async Task<object> GetReportAsync(
        [Description("The report's GUID id.")] Guid reportId,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.ScheduledJobManagement.Detail);

        var response = await _mediator.Send(new GetMetricReportDetailQuery { Id = reportId }, cancellationToken);

        if (response.Data == null)
            throw new McpException($"No report found with id {reportId}.");

        return Unwrap(response.Data);
    }

    /// <summary>
    /// Returns a report with its <c>Data</c> payload parsed into real JSON rather than left as a string.
    /// </summary>
    /// <remarks>
    /// The column is jsonb but the DTO types it as <c>string</c>, so serializing the DTO produces a JSON string
    /// containing escaped JSON - <c>"{\"buckets\":[...]}"</c>. Reading a value out of that means unescaping and
    /// parsing it by hand, and a report is exactly the kind of nested series where doing that by eye goes wrong
    /// quietly: the number comes back plausible and belongs to the wrong bucket.
    /// <para>
    /// Parsing here costs one pass over a payload that is about to be sent anyway. A payload that is not valid
    /// JSON is passed through untouched rather than failing the call - a malformed report is still worth seeing,
    /// and the failure it points at is the reporter worker's, not this tool's.
    /// </para>
    /// </remarks>
    private static object Unwrap(MetricReportDetailDto report)
    {
        object data = report.Data;

        if (!string.IsNullOrWhiteSpace(report.Data))
        {
            try
            {
                using var document = JsonDocument.Parse(report.Data);

                // Clone detaches the element from the document's pooled buffers, so it stays valid past the dispose.
                data = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Left as the raw string, and flagged below so the shape change is not silent.
            }
        }

        return new
        {
            report.Id,
            report.MetricType,
            report.DisplayName,
            report.Description,
            report.PeriodStartTime,
            report.PeriodEndTime,
            report.GeneratedAt,
            report.Tags,
            ageMinutes = Math.Round((DateTime.UtcNow - report.GeneratedAt).TotalMinutes, 1),
            data,
            dataIsRawString = data is string
        };
    }

    #endregion

    #region Infrastructure health

    /// <summary>
    /// Gets overall system health.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>Health of the scheduler and its dependencies.</returns>
    [McpServerTool(Name = "get_system_health", ReadOnly = true)]
    [Description("Gets the health of Milvaion and its dependencies - database, Redis, RabbitMQ. When several unrelated jobs start failing at once, check this before investigating any of them individually: the cause is usually one dependency rather than the jobs.")]
    public async Task<object> GetSystemHealthAsync(CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.SystemAdministration.List);

        var response = await _adminService.GetSystemHealthAsync(cancellationToken);

        return response.Data;
    }

    /// <summary>
    /// Gets RabbitMQ queue statistics.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>Depth and consumer counts per queue.</returns>
    [McpServerTool(Name = "get_queue_stats", ReadOnly = true)]
    [Description("Gets RabbitMQ queue depths and consumer counts. A queue that keeps growing means jobs are being dispatched faster than workers consume them - the jobs are not broken, there is simply not enough worker capacity. A queue with zero consumers means no worker is listening at all.")]
    public async Task<object> GetQueueStatsAsync(CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.SystemAdministration.List);

        var response = await _adminService.GetQueueStatsAsync(cancellationToken);

        return new { queues = response.Data };
    }

    /// <summary>
    /// Gets detail for one queue.
    /// </summary>
    /// <param name="queueName">Queue name, as returned by get_queue_stats.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Queue depth detail.</returns>
    [McpServerTool(Name = "get_queue_info", ReadOnly = true)]
    [Description("Gets depth detail for a single RabbitMQ queue by name, as listed by get_queue_stats.")]
    public async Task<object> GetQueueInfoAsync(
        [Description("The queue name.")] string queueName,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.SystemAdministration.List);

        var response = await _adminService.GetQueueInfoAsync(queueName, cancellationToken);

        if (response.Data == null)
            throw new McpException($"No queue found named '{queueName}'.");

        return response.Data;
    }

    /// <summary>
    /// Gets aggregate job statistics.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>Job and execution counters.</returns>
    [McpServerTool(Name = "get_job_statistics", ReadOnly = true)]
    [Description("Gets aggregate counters across all jobs and executions. Broader than get_overview and useful for capacity questions rather than incident triage.")]
    public async Task<object> GetJobStatisticsAsync(CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.SystemAdministration.List);

        var response = await _adminService.GetJobStatisticsAsync(cancellationToken);

        return response.Data;
    }

    /// <summary>
    /// Gets database size, table sizes, index efficiency, cache hit ratio and bloat.
    /// </summary>
    /// <param name="includeTableDetail">Include per-table sizes as well as the summary.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Database statistics.</returns>
    [McpServerTool(Name = "get_database_statistics", ReadOnly = true)]
    [Description("Gets PostgreSQL statistics: overall size, index efficiency, cache hit ratio and table bloat, with per-table sizes when asked for. Reach for this when the dashboard has become slow or the disk is filling - occurrence history grows without limit unless the maintenance worker is retaining properly.")]
    public async Task<object> GetDatabaseStatisticsAsync(
        [Description("Also return per-table sizes. Leave false for just the summary.")] bool includeTableDetail = false,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.SystemAdministration.List);

        var statistics = await _adminService.GetDatabaseStatisticsAsync(cancellationToken);
        var indexEfficiency = await _adminService.GetIndexEfficiencyAsync(cancellationToken);
        var cacheHitRatio = await _adminService.GetCacheHitRatioAsync(cancellationToken);
        var bloat = await _adminService.GetTableBloatAsync(cancellationToken);

        // Table sizes are a long list on a busy installation, so they are opt-in rather than always returned.
        var tableSizes = includeTableDetail
            ? (await _adminService.GetTableSizesAsync(cancellationToken)).Data
            : null;

        return new
        {
            statistics = statistics.Data,
            indexEfficiency = indexEfficiency.Data,
            cacheHitRatio = cacheHitRatio.Data,
            tableBloat = bloat.Data,
            tableSizes
        };
    }

    #endregion

    #region Configuration and metadata

    /// <summary>
    /// Gets the effective system configuration.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>System configuration.</returns>
    [McpServerTool(Name = "get_configuration", ReadOnly = true)]
    [Description("Gets the scheduler's effective configuration - dispatcher interval, zombie detection timeout, retry and auto-disable defaults, retention settings. Useful for explaining behaviour that looks wrong but is actually configured that way, such as a job marked failed after being queued too long.")]
    public async Task<object> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.SystemAdministration.List);

        var response = await _mediator.Send(new GetSystemConfigurationQuery(), cancellationToken);

        return response.Data;
    }

    /// <summary>
    /// Gets internal notifications.
    /// </summary>
    /// <param name="pageNumber">Page number, starting at 1.</param>
    /// <param name="pageSize">Results per page, capped at <see cref="_maxPageSize"/>.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Paged notification list with the total count.</returns>
    [McpServerTool(Name = "list_notifications", ReadOnly = true)]
    [Description("Lists in-app notifications Milvaion has raised, such as alerts about failing jobs or unhealthy workers. This is the record of what the system itself decided was worth telling somebody about, which is a useful cross-check against what the user thinks happened.")]
    public async Task<object> ListNotificationsAsync(
        [Description("Page number, starting at 1.")] int pageNumber = 1,
        [Description("Results per page. Maximum 100.")] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.InternalNotificationManagement.List);

        var response = await _mediator.Send(new GetInternalNotificationListQuery
        {
            PageNumber = pageNumber < 1 ? 1 : pageNumber,
            RowCount = Math.Clamp(pageSize, 1, _maxPageSize)
        }, cancellationToken);

        return new
        {
            totalCount = response.TotalDataCount,
            pageNumber,
            notifications = response.Data
        };
    }

    /// <summary>
    /// Gets the permission catalog.
    /// </summary>
    /// <param name="pageNumber">Page number, starting at 1.</param>
    /// <param name="pageSize">Results per page, capped at <see cref="_maxPageSize"/>.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Paged permission list with the total count.</returns>
    [McpServerTool(Name = "list_permissions", ReadOnly = true)]
    [Description("Lists every permission Milvaion defines, grouped by area. Use this when a tool call was refused for a missing permission and you need to tell the user exactly what to grant the api key.")]
    public async Task<object> ListPermissionsAsync(
        [Description("Page number, starting at 1.")] int pageNumber = 1,
        [Description("Results per page. Maximum 100.")] int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.PermissionManagement.List);

        var response = await _mediator.Send(new GetPermissionListQuery
        {
            PageNumber = pageNumber < 1 ? 1 : pageNumber,
            RowCount = Math.Clamp(pageSize, 1, _maxPageSize)
        }, cancellationToken);

        return new
        {
            totalCount = response.TotalDataCount,
            pageNumber,
            permissions = response.Data
        };
    }

    #endregion
}
