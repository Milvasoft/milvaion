using FluentValidation;

namespace Milvaion.Application.Features.Dashboard.GetDashboard;

/// <summary>
/// Account detail query validations.
/// </summary>
public sealed class GetDashboardQueryValidator : AbstractValidator<GetDashboardQuery>
{
    ///<inheritdoc cref="GetDashboardQueryValidator"/>
    public GetDashboardQueryValidator()
    {
    }
}