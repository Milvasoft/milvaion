using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.MetricReports.DeleteMetricReport;

/// <summary>
/// Deletes metric report.
/// </summary>
/// <param name="metricReportRepository"></param>
public class DeleteMetricReportCommandHandler(IMilvaionRepositoryBase<MetricReport> metricReportRepository) : IInterceptable, ICommandHandler<DeleteMetricReportCommand, Guid>
{
    private readonly IMilvaionRepositoryBase<MetricReport> _metricReportRepository = metricReportRepository;

    /// <inheritdoc />
    public async Task<Response<Guid>> Handle(DeleteMetricReportCommand request, CancellationToken cancellationToken)
    {
        var report = await _metricReportRepository.GetByIdAsync(request.Id, cancellationToken: cancellationToken);

        if (report == null)
            return Response<Guid>.Error(default, "Metric report not found");

        await _metricReportRepository.DeleteAsync(report, cancellationToken: cancellationToken);

        return Response<Guid>.Success(request.Id);
    }
}
