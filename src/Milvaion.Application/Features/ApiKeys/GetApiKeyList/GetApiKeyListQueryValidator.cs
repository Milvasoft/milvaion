using FluentValidation;

namespace Milvaion.Application.Features.ApiKeys.GetApiKeyList;

/// <summary>
/// Api key list query validations.
/// </summary>
public sealed class GetApiKeyListQueryValidator : AbstractValidator<GetApiKeyListQuery>
{
    ///<inheritdoc cref="GetApiKeyListQueryValidator"/>
    public GetApiKeyListQueryValidator()
    {
    }
}
