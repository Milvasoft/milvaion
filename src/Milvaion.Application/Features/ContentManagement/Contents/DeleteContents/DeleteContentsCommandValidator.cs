using FluentValidation;
using Milvaion.Application.Behaviours;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.ContentManagement.Contents.DeleteContents;

/// <summary>
/// Query validations. 
/// </summary>
public sealed class DeleteContentsCommandValidator : AbstractValidator<DeleteContentsCommand>
{
    ///<inheritdoc cref="DeleteContentsCommandValidator"/>
    public DeleteContentsCommandValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.ContentIdList)
            .NotNullOrEmpty(localizer, MessageKey.Content);

        RuleForEach(query => query.ContentIdList)
            .GreaterThan(0)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.Content]]);
    }
}