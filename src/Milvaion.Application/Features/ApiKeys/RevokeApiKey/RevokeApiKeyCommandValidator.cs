using FluentValidation;
using Milvaion.Application.Behaviours;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.ApiKeys.RevokeApiKey;

/// <summary>
/// Api key revocation command validations.
/// </summary>
public sealed class RevokeApiKeyCommandValidator : AbstractValidator<RevokeApiKeyCommand>
{
    ///<inheritdoc cref="RevokeApiKeyCommandValidator"/>
    public RevokeApiKeyCommandValidator(IMilvaLocalizer localizer)
    {
        RuleFor(command => command.ApiKeyId)
            .NotBeDefaultData()
            .WithMessage(localizer[MessageKey.DefaultValueCannotModify]);

        RuleFor(command => command.ApiKeyId)
            .GreaterThan(0)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.ApiKey]]);
    }
}
