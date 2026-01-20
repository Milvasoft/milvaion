using FluentValidation;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.Workers.DeleteWorker;

/// <summary>
/// Account detail query validations.
/// </summary>
public sealed class DeleteWorkerCommandValidator : AbstractValidator<DeleteWorkerCommand>
{
    ///<inheritdoc cref="DeleteWorkerCommandValidator"/>
    public DeleteWorkerCommandValidator(IMilvaLocalizer localizer)
    {
        RuleFor(p => p.WorkerId)
            .NotEmpty()
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.Worker]]);
    }
}