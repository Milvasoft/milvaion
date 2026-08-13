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
        // 0 (or omitted) is allowed: it means "the current user", resolved from the token. SSO users have no
        // local user id to send. A positive value must still be a real id.
        RuleFor(query => query.UserId)
            .GreaterThanOrEqualTo(0)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.User]]);
    }
}