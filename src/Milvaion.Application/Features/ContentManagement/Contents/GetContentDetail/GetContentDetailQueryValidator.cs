using FluentValidation;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.ContentManagement.Contents.GetContentDetail;

/// <summary>
/// Query validations. 
/// </summary>
public sealed class GetContentDetailQueryValidator : AbstractValidator<GetContentDetailQuery>
{
    ///<inheritdoc cref="GetContentDetailQueryValidator"/>
    public GetContentDetailQueryValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.ContentId)
            .GreaterThan(0)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.Content]]);
    }
}