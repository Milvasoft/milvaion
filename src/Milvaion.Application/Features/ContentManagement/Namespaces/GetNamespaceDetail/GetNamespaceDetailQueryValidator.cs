using FluentValidation;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.ContentManagement.Namespaces.GetNamespaceDetail;

/// <summary>
/// Account detail query validations. 
/// </summary>
public sealed class GetNamespaceDetailQueryValidator : AbstractValidator<GetNamespaceDetailQuery>
{
    ///<inheritdoc cref="GetNamespaceDetailQueryValidator"/>
    public GetNamespaceDetailQueryValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.NamespaceId)
            .GreaterThan(0)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.Namespace]]);
    }
}