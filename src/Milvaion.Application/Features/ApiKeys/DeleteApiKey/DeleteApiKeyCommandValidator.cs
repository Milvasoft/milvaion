using FluentValidation;
using Milvaion.Application.Behaviours;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.ApiKeys.DeleteApiKey;

/// <summary>
/// Api key deletion command validations.
/// </summary>
public sealed class DeleteApiKeyCommandValidator : AbstractValidator<DeleteApiKeyCommand>
{
    ///<inheritdoc cref="DeleteApiKeyCommandValidator"/>
    public DeleteApiKeyCommandValidator(IMilvaLocalizer localizer)
    {
        RuleFor(command => command.ApiKeyId)
            .NotBeDefaultData()
            .WithMessage(localizer[MessageKey.DefaultValueCannotModify]);

        RuleFor(command => command.ApiKeyId)
            .GreaterThan(0)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.ApiKey]]);
    }
}
