using FluentValidation;

namespace Milvaion.Application.Features.MetricReports.GetMetricReportSummaryList;

/// <inheritdoc />
public class GetMetricReportSummaryListQueryValidator : AbstractValidator<GetMetricReportSummaryListQuery>
{
    /// <inheritdoc />
    public GetMetricReportSummaryListQueryValidator()
    {
        RuleFor(x => x.RowCount)
            .GreaterThan(0).WithMessage("Row count must be greater than 0")
            .LessThanOrEqualTo(1000).WithMessage("Row count cannot exceed 1000");
    }
}
