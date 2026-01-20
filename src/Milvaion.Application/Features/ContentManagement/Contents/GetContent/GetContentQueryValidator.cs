using FluentValidation;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.ContentManagement.Contents.GetContent;

/// <summary>
/// Query validations. 
/// </summary>
public sealed class GetContentQueryValidator : AbstractValidator<GetContentQuery>
{
    ///<inheritdoc cref="GetContentQueryValidator"/>
    public GetContentQueryValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.NamespaceSlug)
            .NotNull()
            .NotEmpty()
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.Namespace]]);

        RuleFor(query => query.Query)
            .NotNull()
            .NotEmpty()
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.Query]]);

        RuleFor(query => query.QueryType)
            .IsInEnum()
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.QueryType]]);
    }
}