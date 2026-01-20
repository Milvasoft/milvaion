using FluentValidation;
using Milvaion.Application.Features.ContentManagement.Contents.CreateBulkContent;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.ContentManagement.Contents.CreateContent;

/// <summary>
/// Query validations. 
/// </summary>
public sealed class CreateContentCommandValidator : AbstractValidator<CreateContentCommand>
{
    ///<inheritdoc cref="CreateContentCommandValidator"/>
    public CreateContentCommandValidator(IMilvaLocalizer localizer)
    {
        RuleFor(dto => dto).SetValidator(new CreateContentDtoValidator(localizer));
    }
}