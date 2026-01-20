using FluentValidation;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.Account.ChangePassword;

/// <summary>
/// Account detail query validations. 
/// </summary>
public sealed class ChangePasswordCommandValidator : AbstractValidator<ChangePasswordCommand>
{
    ///<inheritdoc cref="ChangePasswordCommandValidator"/>
    public ChangePasswordCommandValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.UserName)
            .NotEmpty()
            .NotNull()
            .WithMessage(localizer[MessageKey.IdentityInvalidUsername]);

        RuleFor(query => query.OldPassword)
            .NotEmpty()
            .NotNull()
            .WithMessage(localizer[MessageKey.CannotBeEmpty, localizer[nameof(ChangePasswordCommand.OldPassword)]]);

        RuleFor(query => query.NewPassword)
            .NotEmpty()
            .NotNull()
            .WithMessage(localizer[MessageKey.CannotBeEmpty, localizer[nameof(ChangePasswordCommand.NewPassword)]]);
    }
}