using FluentValidation;
using Milvaion.Application.Behaviours;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.ApiKeys.UpdateApiKey;

/// <summary>
/// Api key update command validations.
/// </summary>
public sealed class UpdateApiKeyCommandValidator : AbstractValidator<UpdateApiKeyCommand>
{
    ///<inheritdoc cref="UpdateApiKeyCommandValidator"/>
    public UpdateApiKeyCommandValidator(IMilvaLocalizer localizer)
    {
        RuleFor(command => command.Id)
            .NotBeDefaultData()
            .WithMessage(localizer[MessageKey.DefaultValueCannotModify]);

        RuleFor(command => command.Id)
            .GreaterThan(0)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.ApiKey]]);

        RuleFor(command => command.Name)
            .NotNullOrEmpty(localizer, MessageKey.GlobalName)
            .When(command => command.Name.IsUpdated);
    }
}
