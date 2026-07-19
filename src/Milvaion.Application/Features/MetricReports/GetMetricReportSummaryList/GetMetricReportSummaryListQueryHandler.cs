using Milvaion.Application.Dtos.MetricReportDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using System.Linq.Expressions;

namespace Milvaion.Application.Features.MetricReports.GetMetricReportSummaryList;

/// <summary>
/// Gets metric report metadata without the report payloads, optionally filtered by metric type.
/// </summary>
/// <param name="metricReportRepository"></param>
public class GetMetricReportSummaryListQueryHandler(IMilvaionRepositoryBase<MetricReport> metricReportRepository) : IInterceptable, IListQueryHandler<GetMetricReportSummaryListQuery, MetricReportSummaryDto>
{
    private readonly IMilvaionRepositoryBase<MetricReport> _metricReportRepository = metricReportRepository;

    /// <inheritdoc />
    public async Task<ListResponse<MetricReportSummaryDto>> Handle(GetMetricReportSummaryListQuery request, CancellationToken cancellationToken)
    {
        Expression<Func<MetricReport, bool>> predicate = null;

        if (!string.IsNullOrWhiteSpace(request.MetricType))
        {
            predicate = r => r.MetricType == request.MetricType;
        }

        var response = await _metricReportRepository.GetAllAsync(
            request,
            condition: predicate,
            projection: MetricReportSummaryDto.Projection,
            cancellationToken: cancellationToken);

        // Age is relative to the moment of reading, so it is stamped here rather than translated into SQL. One
        // timestamp for the whole page, so two rows generated together do not appear minutes apart because the
        // loop happened to straddle a tick.
        var now = DateTime.UtcNow;

        foreach (var report in response.Data ?? [])
            report.AgeMinutes = Math.Round((now - report.GeneratedAt).TotalMinutes, 1);

        return response;
    }
}
