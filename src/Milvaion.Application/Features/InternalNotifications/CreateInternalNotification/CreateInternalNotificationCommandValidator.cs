using FluentValidation;
using Milvaion.Application.Behaviours;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.InternalNotifications.CreateInternalNotification;

/// <summary>
/// Account detail query validations. 
/// </summary>
public sealed class CreateInternalNotificationCommandValidator : AbstractValidator<CreateInternalNotificationCommand>
{
    ///<inheritdoc cref="CreateInternalNotificationCommandValidator"/>
    public CreateInternalNotificationCommandValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.Type)
            .IsInEnum()
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[nameof(NotificationType)]]);

        RuleFor(query => query.RelatedEntity)
            .IsInEnum()
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[nameof(NotificationEntity)]]);

        RuleFor(query => query.Recipients)
            .NotNullOrEmpty(localizer, MessageKey.User);

        RuleForEach(query => query.Recipients)
            .NotNullOrEmpty(localizer, MessageKey.User);
    }
}