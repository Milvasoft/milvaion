using FluentValidation;
using Milvaion.Application.Behaviours;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.Account.InternalNotifications.MarkNotificationsAsSeen;

/// <summary>
/// Query validations. 
/// </summary>
public sealed class MarkNotificationsAsSeenCommandValidator : AbstractValidator<MarkNotificationsAsSeenCommand>
{
    ///<inheritdoc cref="MarkNotificationsAsSeenCommandValidator"/>
    public MarkNotificationsAsSeenCommandValidator(IMilvaLocalizer localizer)
    {
        RuleForEach(query => query.NotificationIdList)
            .NotBeDefaultData()
            .WithMessage(localizer[MessageKey.DefaultValueCannotModify]);

        RuleForEach(query => query.NotificationIdList)
            .GreaterThan(0)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.InternalNotification]]);
    }
}