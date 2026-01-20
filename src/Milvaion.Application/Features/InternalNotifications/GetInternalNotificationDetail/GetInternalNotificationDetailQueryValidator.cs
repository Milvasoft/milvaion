using FluentValidation;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.InternalNotifications.GetInternalNotificationDetail;

/// <summary>
/// Account detail query validations. 
/// </summary>
public sealed class GetInternalNotificationDetailQueryValidator : AbstractValidator<GetInternalNotificationDetailQuery>
{
    ///<inheritdoc cref="GetInternalNotificationDetailQueryValidator"/>
    public GetInternalNotificationDetailQueryValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.InternalNotificationId)
            .GreaterThan(0)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.InternalNotification]]);
    }
}