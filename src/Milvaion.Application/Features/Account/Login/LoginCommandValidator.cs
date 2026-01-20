using FluentValidation;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.Account.Login;

/// <summary>
/// Account detail query validations. 
/// </summary>
public sealed class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    ///<inheritdoc cref="LoginCommandValidator"/>
    public LoginCommandValidator(IMilvaLocalizer localizer)
    {
        ClassLevelCascadeMode = CascadeMode.Stop;

        RuleFor(query => query.Password)
            .NotEmpty()
            .NotNull()
            .WithMessage(localizer[MessageKey.CannotBeEmpty, localizer[nameof(LoginCommand.Password)]]);

        RuleFor(query => query.DeviceId)
            .NotEmpty()
            .NotNull()
            .WithMessage(localizer[MessageKey.PossibleUIError]);
    }
}