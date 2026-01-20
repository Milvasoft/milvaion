using FluentValidation;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.ScheduledJobs.DeleteJobOccurrence;

/// <summary>
/// Validator for DeleteJobOccurrenceCommand.
/// </summary>
public class DeleteJobOccurrenceCommandValidator : AbstractValidator<DeleteJobOccurrenceCommand>
{
    /// <inheritdoc cref="DeleteJobOccurrenceCommandValidator"/>
    public DeleteJobOccurrenceCommandValidator(IMilvaLocalizer localizer)
    {
        RuleFor(x => x.OccurrenceIdList)
            .NotEmpty()
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, MessageKey.OccurrenceId]);

        RuleForEach(query => query.OccurrenceIdList)
            .Must(id => Guid.Empty != id)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.Occurrence]]);
    }
}
