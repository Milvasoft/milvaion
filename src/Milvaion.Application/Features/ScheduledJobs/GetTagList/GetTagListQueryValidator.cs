using FluentValidation;

namespace Milvaion.Application.Features.ScheduledJobs.GetTagList;

/// <summary>
/// Account detail query validations.
/// </summary>
public sealed class GetTagListQueryValidator : AbstractValidator<GetTagListQuery>
{
    ///<inheritdoc cref="GetTagListQueryValidator"/>
    public GetTagListQueryValidator()
    {
    }
}