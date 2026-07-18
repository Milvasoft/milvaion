using FluentValidation;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.ApiKeys.GetApiKeyDetail;

/// <summary>
/// Api key detail query validations.
/// </summary>
public sealed class GetApiKeyDetailQueryValidator : AbstractValidator<GetApiKeyDetailQuery>
{
    ///<inheritdoc cref="GetApiKeyDetailQueryValidator"/>
    public GetApiKeyDetailQueryValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.ApiKeyId)
            .GreaterThan(0)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.ApiKey]]);
    }
}
