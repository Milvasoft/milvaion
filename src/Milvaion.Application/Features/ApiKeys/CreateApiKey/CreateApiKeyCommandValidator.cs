using FluentValidation;
using Milvaion.Application.Behaviours;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.ApiKeys.CreateApiKey;

/// <summary>
/// Api key creation command validations.
/// </summary>
public sealed class CreateApiKeyCommandValidator : AbstractValidator<CreateApiKeyCommand>
{
    ///<inheritdoc cref="CreateApiKeyCommandValidator"/>
    public CreateApiKeyCommandValidator(IMilvaLocalizer localizer)
    {
        RuleFor(command => command.Name)
            .NotNullOrEmpty(localizer, MessageKey.GlobalName);

        RuleFor(command => command.Name)
            .MaximumLength(100);

        RuleFor(command => command.Description)
            .MaximumLength(500);

        RuleFor(command => command.ExpiresAt)
            .GreaterThan(DateTime.UtcNow)
            .When(command => command.ExpiresAt.HasValue)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.GlobalName]]);
    }
}
