using FluentValidation;
using Milvaion.Application.Behaviours;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.Pages.GetPageAccessibilityForCurrentUser;

/// <summary>
/// Account detail query validations. 
/// </summary>
public sealed class GetPageAccessibilityForCurrentUserQueryValidator : AbstractValidator<GetPageAccessibilityForCurrentUserQuery>
{
    ///<inheritdoc cref="GetPageAccessibilityForCurrentUserQueryValidator"/>
    public GetPageAccessibilityForCurrentUserQueryValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.PageName)
            .NotNullOrEmpty(localizer, MessageKey.Page);
    }
}