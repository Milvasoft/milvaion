using FluentValidation;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.Account.AccountDetail;

/// <summary>
/// Account detail query validations. 
/// </summary>
public sealed class AccountDetailQueryValidator : AbstractValidator<AccountDetailQuery>
{
    ///<inheritdoc cref="AccountDetailQueryValidator"/>
    public AccountDetailQueryValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.UserId)
            .GreaterThan(0)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.User]]);
    }
}