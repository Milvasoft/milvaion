using FluentValidation;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.ContentManagement.ResourceGroups.GetResourceGroupDetail;

/// <summary>
/// Query validations. 
/// </summary>
public sealed class GetResourceGroupDetailQueryValidator : AbstractValidator<GetResourceGroupDetailQuery>
{
    ///<inheritdoc cref="GetResourceGroupDetailQueryValidator"/>
    public GetResourceGroupDetailQueryValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.ResourceGroupId)
            .GreaterThan(0)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.Namespace]]);
    }
}