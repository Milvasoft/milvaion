using FluentValidation;
using Milvaion.Application.Behaviours;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.Users.UpdateUser;

/// <summary>
/// Account detail query validations. 
/// </summary>
public sealed class UpdateUserCommandValidator : AbstractValidator<UpdateUserCommand>
{
    ///<inheritdoc cref="UpdateUserCommandValidator"/>
    public UpdateUserCommandValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.Id)
            .NotBeDefaultData()
            .WithMessage(localizer[MessageKey.DefaultValueCannotModify]);

        RuleFor(query => query.Id)
            .GreaterThan(0)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.User]]);

        RuleFor(query => query.Name.Value)
            .MaximumLength(100)
            .WithMessage(localizer[MessageKey.MaxLengthReached, localizer[MessageKey.GlobalNameSurname], 100])
            .When(q => q.Name.IsUpdated);

        RuleFor(query => query.Surname.Value)
            .MaximumLength(100)
            .WithMessage(localizer[MessageKey.MaxLengthReached, localizer[MessageKey.GlobalNameSurname], 100])
            .When(q => q.Surname.IsUpdated);
    }
}