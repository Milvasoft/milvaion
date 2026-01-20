using FluentValidation;
using Milvaion.Application.Behaviours;
using Milvaion.Application.Features.ContentManagement.Contents.CreateBulkContent;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.ContentManagement.Contents.UpdateContent;

/// <summary>
/// Query validations. 
/// </summary>
public sealed class UpdateContentCommandValidator : AbstractValidator<UpdateContentCommand>
{
    ///<inheritdoc cref="UpdateContentCommandValidator"/>
    public UpdateContentCommandValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.Id)
            .GreaterThan(0)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.Content]]);

        RuleFor(query => query.Value)
            .NotNullOrEmpty(localizer, MessageKey.GlobalValue)
            .When(q => q.Value.IsUpdated);

        RuleForEach(query => query.Medias.Value)
            .NotNullOrEmpty(localizer, MessageKey.Media)
            .When(query => query.Value != null && query.Value.IsUpdated)
            .SetValidator(new UpsertMediaValidator(localizer));
    }
}