using FluentValidation;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.Workers.GetWorkerDetail;

/// <summary>
/// Account detail query validations.
/// </summary>
public sealed class GetWorkerDetailQueryValidator : AbstractValidator<GetWorkerDetailQuery>
{
    ///<inheritdoc cref="GetWorkerDetailQueryValidator"/>
    public GetWorkerDetailQueryValidator(IMilvaLocalizer localizer)
    {
        RuleFor(p => p.WorkerId)
            .NotEmpty()
            .WithMessage(localizer[nameof(MessageKey.PleaseSendCorrect), localizer[nameof(MessageKey.WorkerId)]]);
    }
}