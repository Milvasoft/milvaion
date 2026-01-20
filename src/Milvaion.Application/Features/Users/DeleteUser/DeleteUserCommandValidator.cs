using FluentValidation;
using Milvaion.Application.Behaviours;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.Users.DeleteUser;

/// <summary>
/// Account detail query validations. 
/// </summary>
public sealed class DeleteUserCommandValidator : AbstractValidator<DeleteUserCommand>
{
    ///<inheritdoc cref="DeleteUserCommandValidator"/>
    public DeleteUserCommandValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.UserId)
            .NotBeDefaultData()
            .WithMessage(localizer[MessageKey.DefaultValueCannotModify]);

        RuleFor(query => query.UserId)
            .GreaterThan(0)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.User]]);
    }
}