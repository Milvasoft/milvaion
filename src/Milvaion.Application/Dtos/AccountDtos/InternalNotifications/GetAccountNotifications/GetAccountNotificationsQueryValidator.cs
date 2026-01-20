using FluentValidation;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Dtos.AccountDtos.InternalNotifications.GetAccountNotifications;

/// <summary>
/// Query validations. 
/// </summary>
public sealed class GetAccountNotificationsQueryValidator : AbstractValidator<GetAccountNotificationsQuery>
{
    ///<inheritdoc cref="GetAccountNotificationsQueryValidator"/>
    public GetAccountNotificationsQueryValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.UserId)
            .GreaterThan(0)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.User]]);
    }
}