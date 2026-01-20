using FluentValidation;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.ScheduledJobs.DeleteScheduledJob;

/// <summary>
/// Account detail query validations.
/// </summary>
public sealed class DeleteScheduledJobCommandValidator : AbstractValidator<DeleteScheduledJobCommand>
{
    ///<inheritdoc cref="DeleteScheduledJobCommandValidator"/>
    public DeleteScheduledJobCommandValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.JobId)
            .Must(id => Guid.Empty != id)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.Job]]);
    }
}