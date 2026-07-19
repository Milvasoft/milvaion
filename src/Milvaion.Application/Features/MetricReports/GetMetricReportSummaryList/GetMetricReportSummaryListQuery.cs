using Milvaion.Application.Dtos.MetricReportDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Request;

namespace Milvaion.Application.Features.MetricReports.GetMetricReportSummaryList;

/// <summary>
/// Gets metric report metadata without the report payloads, optionally filtered by metric type.
/// </summary>
public record GetMetricReportSummaryListQuery : ListRequest, IListRequestQuery<MetricReportSummaryDto>
{
    /// <summary>
    /// Type of metric.
    /// </summary>
    public string MetricType { get; set; }
}
