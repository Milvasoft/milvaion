using Microsoft.EntityFrameworkCore;
using Milvaion.Application.Dtos.MetricReportDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.MetricReports.GetLatestMetricReport;

/// <summary>
/// Gets latest metric report for a specified metric type.
/// </summary>
/// <param name="milvaionDbContextAccessor"></param>
public class GetLatestMetricReportQueryHandler(IMilvaionDbContextAccessor milvaionDbContextAccessor) : IInterceptable, IQueryHandler<GetLatestMetricReportQuery, MetricReportDetailDto>
{
    private readonly IMilvaionDbContextAccessor _milvaionDbContextAccessor = milvaionDbContextAccessor;

    /// <inheritdoc />
    public async Task<Response<MetricReportDetailDto>> Handle(GetLatestMetricReportQuery request, CancellationToken cancellationToken)
    {
        // Reports carry a large payload, so only the newest one may be read.
        var report = await _milvaionDbContextAccessor.GetDbContext()
                                                     .Set<MetricReport>()
                                                     .AsNoTracking()
                                                     .Where(r => r.MetricType == request.MetricType)
                                                     .OrderByDescending(r => r.GeneratedAt)
                                                     .Select(MetricReportDetailDto.Projection)
                                                     .FirstOrDefaultAsync(cancellationToken);

        if (report == null)
            return Response<MetricReportDetailDto>.Error(default, "Metric report not found for the specified type");

        return Response<MetricReportDetailDto>.Success(report);
    }
}
