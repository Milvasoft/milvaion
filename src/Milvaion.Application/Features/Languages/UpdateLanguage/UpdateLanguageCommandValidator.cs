using FluentValidation;
using Milvaion.Application.Behaviours;
using Milvaion.Application.Features.Roles.UpdateRole;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.Languages.UpdateLanguage;

/// <summary>
/// Account detail query validations. 
/// </summary>
public sealed class UpdateLanguageCommandValidator : AbstractValidator<UpdateLanguageCommand>
{
    ///<inheritdoc cref="UpdateRoleCommandValidator"/>
    public UpdateLanguageCommandValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.Id)
            .NotBeDefaultData()
            .WithMessage(localizer[MessageKey.DefaultValueCannotModify]);

        RuleFor(query => query.Id)
            .GreaterThan(0)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.Language]]);
    }
}