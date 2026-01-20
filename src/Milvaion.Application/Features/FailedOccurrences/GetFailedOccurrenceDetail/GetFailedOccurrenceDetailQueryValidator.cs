using FluentValidation;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.FailedOccurrences.GetFailedOccurrenceDetail;

/// <summary>
/// Account detail query validations.
/// </summary>
public sealed class GetFailedOccurrenceDetailQueryValidator : AbstractValidator<GetFailedOccurrenceDetailQuery>
{
    ///<inheritdoc cref="GetFailedOccurrenceDetailQueryValidator"/>
    public GetFailedOccurrenceDetailQueryValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.FailedOccurrenceId)
            .Must(id => Guid.Empty != id)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.Job]]);
    }
}