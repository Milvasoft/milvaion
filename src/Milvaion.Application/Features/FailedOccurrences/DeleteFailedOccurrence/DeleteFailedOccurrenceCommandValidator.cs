using FluentValidation;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.FailedOccurrences.DeleteFailedOccurrence;

/// <summary>
/// Account detail query validations.
/// </summary>
public sealed class DeleteFailedOccurrenceCommandValidator : AbstractValidator<DeleteFailedOccurrenceCommand>
{
    ///<inheritdoc cref="DeleteFailedOccurrenceCommandValidator"/>
    public DeleteFailedOccurrenceCommandValidator(IMilvaLocalizer localizer)
    {
        RuleForEach(query => query.FailedOccurrenceIdList)
            .Must(id => Guid.Empty != id)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.Job]]);
    }
}