using FluentValidation;

namespace Milvaion.Application.Features.ScheduledJobs.GetUpcomingExecutionList;

/// <summary>
/// Upcoming execution list query validations.
/// </summary>
public sealed class GetUpcomingExecutionListQueryValidator : AbstractValidator<GetUpcomingExecutionListQuery>
{
    ///<inheritdoc cref="GetUpcomingExecutionListQueryValidator"/>
    public GetUpcomingExecutionListQueryValidator()
    {
        // A month ahead is already beyond what anyone reads off a timeline, and the
        // window is what bounds the Redis range read.
        RuleFor(q => q.WithinHours).InclusiveBetween(1, 720);

        RuleFor(q => q.Limit).InclusiveBetween(1, 500);

        RuleFor(q => q.SearchTerm).MaximumLength(200);
    }
}
