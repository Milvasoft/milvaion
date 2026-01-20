using FluentValidation;
using Milvaion.Application.Behaviours;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.InternalNotifications.DeleteInternalNotification;

/// <summary>
/// Account detail query validations. 
/// </summary>
public sealed class DeleteInternalNotificationCommandValidator : AbstractValidator<DeleteInternalNotificationCommand>
{
    ///<inheritdoc cref="DeleteInternalNotificationCommandValidator"/>
    public DeleteInternalNotificationCommandValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.InternalNotificationId)
            .NotBeDefaultData()
            .WithMessage(localizer[MessageKey.DefaultValueCannotModify]);

        RuleFor(query => query.InternalNotificationId)
            .GreaterThan(0)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.InternalNotification]]);
    }
}