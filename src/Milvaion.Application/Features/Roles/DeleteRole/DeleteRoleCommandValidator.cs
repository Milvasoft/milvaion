using FluentValidation;
using Milvaion.Application.Behaviours;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.Roles.DeleteRole;

/// <summary>
/// Account detail query validations. 
/// </summary>
public sealed class DeleteRoleCommandValidator : AbstractValidator<DeleteRoleCommand>
{
    ///<inheritdoc cref="DeleteRoleCommandValidator"/>
    public DeleteRoleCommandValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.RoleId)
            .NotBeDefaultData()
            .WithMessage(localizer[MessageKey.DefaultValueCannotModify]);

        RuleFor(query => query.RoleId)
            .GreaterThan(0)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.Role]]);
    }
}