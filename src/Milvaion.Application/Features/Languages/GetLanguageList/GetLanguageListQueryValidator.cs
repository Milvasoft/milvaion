using FluentValidation;

namespace Milvaion.Application.Features.Languages.GetLanguageList;

/// <summary>
/// Account detail query validations. 
/// </summary>
public sealed class GetLanguageListQueryValidator : AbstractValidator<GetLanguageListQuery>
{
    ///<inheritdoc cref="GetLanguageListQueryValidator"/>
    public GetLanguageListQueryValidator()
    {
    }
}