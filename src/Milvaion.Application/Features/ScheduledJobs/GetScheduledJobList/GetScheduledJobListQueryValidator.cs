using FluentValidation;

namespace Milvaion.Application.Features.ScheduledJobs.GetScheduledJobList;

/// <summary>
/// Account detail query validations. 
/// </summary>
public sealed class GetScheduledJobListQueryValidator : AbstractValidator<GetScheduledJobListQuery>
{
    ///<inheritdoc cref="GetScheduledJobListQueryValidator"/>
    public GetScheduledJobListQueryValidator()
    {
    }
}