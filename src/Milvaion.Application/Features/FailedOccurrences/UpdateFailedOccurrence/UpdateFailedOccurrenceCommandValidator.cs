using FluentValidation;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.FailedOccurrences.UpdateFailedOccurrence;

/// <summary>
/// Account detail query validations.
/// </summary>
public sealed class UpdateFailedOccurrenceCommandValidator : AbstractValidator<UpdateFailedOccurrenceCommand>
{
    ///<inheritdoc cref="UpdateFailedOccurrenceCommandValidator"/>
    public UpdateFailedOccurrenceCommandValidator(IMilvaLocalizer localizer)
    {
        RuleForEach(query => query.IdList)
           .Must(id => Guid.Empty != id)
           .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.Job]]);
    }
}