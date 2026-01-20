using FluentValidation;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.ScheduledJobs.GetJobOccurenceDetail;

/// <summary>
/// Account detail query validations.
/// </summary>
public sealed class GetJobOccurrenceDetailQueryValidator : AbstractValidator<GetJobOccurrenceDetailQuery>
{
    ///<inheritdoc cref="GetJobOccurrenceDetailQueryValidator"/>
    public GetJobOccurrenceDetailQueryValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.OccurrenceId)
            .Must(id => Guid.Empty != id)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.Job]]);
    }
}