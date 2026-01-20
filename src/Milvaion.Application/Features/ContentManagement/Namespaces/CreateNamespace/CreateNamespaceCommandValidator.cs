using FluentValidation;
using Milvaion.Application.Behaviours;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.ContentManagement.Namespaces.CreateNamespace;

/// <summary>
/// Account detail query validations. 
/// </summary>
public sealed class CreateNamespaceCommandValidator : AbstractValidator<CreateNamespaceCommand>
{
    ///<inheritdoc cref="CreateNamespaceCommandValidator"/>
    public CreateNamespaceCommandValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.Name)
            .NotNullOrEmpty(localizer, MessageKey.GlobalName);
    }
}