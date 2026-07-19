using FluentValidation;

namespace Milvaion.Application.Features.ScheduledJobs.GetJobOccurrenceLogList;

/// <summary>
/// Log search query validations.
/// </summary>
public sealed class GetJobOccurrenceLogListQueryValidator : AbstractValidator<GetJobOccurrenceLogListQuery>
{
    ///<inheritdoc cref="GetJobOccurrenceLogListQueryValidator"/>
    public GetJobOccurrenceLogListQueryValidator()
    {
        RuleFor(q => q.PageNumber).GreaterThan(0);

        RuleFor(q => q.RowCount).InclusiveBetween(1, 200);

        RuleFor(q => q.SearchTerm).MaximumLength(500);

        RuleFor(q => q.Until).GreaterThanOrEqualTo(q => q.Since)
                             .When(q => q.Since.HasValue && q.Until.HasValue)
                             .WithMessage("Until must not be earlier than Since.");
    }
}
