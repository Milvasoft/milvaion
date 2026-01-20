using FluentValidation;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.InternalNotifications.UpdateInternalNotification;

/// <summary>
/// Account detail query validations. 
/// </summary>
public sealed class UpdateInternalNotificationCommandValidator : AbstractValidator<UpdateInternalNotificationCommand>
{
    ///<inheritdoc cref="UpdateInternalNotificationCommandValidator"/>
    public UpdateInternalNotificationCommandValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.Type)
            .IsInEnum()
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[nameof(NotificationType)]]);

        RuleFor(query => query.RelatedEntity)
            .IsInEnum()
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[nameof(NotificationEntity)]]);
    }
}