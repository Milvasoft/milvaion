using FluentValidation;
using Milvaion.Application.Behaviours;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.Users.CreateUser;

/// <summary>
/// Account detail query validations. 
/// </summary>
public sealed class CreateUserCommandValidator : AbstractValidator<CreateUserCommand>
{
    ///<inheritdoc cref="CreateUserCommandValidator"/>
    public CreateUserCommandValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.UserName)
            .NotNullOrEmpty(localizer, MessageKey.GlobalUsername);

        RuleFor(query => query.Password)
            .NotNullOrEmpty(localizer, MessageKey.GlobalPassword);

        RuleFor(query => query.Name)
            .MaximumLength(70)
            .WithMessage(localizer[MessageKey.MaxLengthReached, localizer[MessageKey.GlobalName], 70]);

        RuleFor(query => query.Surname)
            .MaximumLength(70)
            .WithMessage(localizer[MessageKey.MaxLengthReached, localizer[MessageKey.GlobalName], 70]);

        RuleFor(query => query.UserType)
            .IsInEnum()
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.GlobalUserType]]);

        RuleFor(query => query.RoleIdList)
            .NotNullOrEmpty(localizer, MessageKey.Role);

        RuleForEach(query => query.RoleIdList)
            .GreaterThan(0)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.Role]]);
    }
}