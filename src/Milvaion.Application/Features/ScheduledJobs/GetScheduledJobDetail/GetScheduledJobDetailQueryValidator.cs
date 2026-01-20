using FluentValidation;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.ScheduledJobs.GetScheduledJobDetail;

/// <summary>
/// Account detail query validations.
/// </summary>
public sealed class GetScheduledJobDetailQueryValidator : AbstractValidator<GetScheduledJobDetailQuery>
{
    ///<inheritdoc cref="GetScheduledJobDetailQueryValidator"/>
    public GetScheduledJobDetailQueryValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.JobId)
            .Must(id => Guid.Empty != id)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.Job]]);
    }
}