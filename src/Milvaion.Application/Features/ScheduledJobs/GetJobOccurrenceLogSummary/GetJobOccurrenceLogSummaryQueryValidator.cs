using FluentValidation;

namespace Milvaion.Application.Features.ScheduledJobs.GetJobOccurrenceLogSummary;

/// <summary>
/// Log summary query validations.
/// </summary>
public sealed class GetJobOccurrenceLogSummaryQueryValidator : AbstractValidator<GetJobOccurrenceLogSummaryQuery>
{
    ///<inheritdoc cref="GetJobOccurrenceLogSummaryQueryValidator"/>
    public GetJobOccurrenceLogSummaryQueryValidator()
    {
        RuleFor(q => q.TopCount).InclusiveBetween(1, 50);

        RuleFor(q => q.SearchTerm).MaximumLength(500);

        RuleFor(q => q.Until).GreaterThanOrEqualTo(q => q.Since)
                             .When(q => q.Since.HasValue && q.Until.HasValue)
                             .WithMessage("Until must not be earlier than Since.");
    }
}
