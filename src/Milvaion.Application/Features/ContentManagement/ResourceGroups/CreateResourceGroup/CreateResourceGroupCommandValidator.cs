using FluentValidation;
using Milvaion.Application.Behaviours;
using Milvaion.Application.Features.ContentManagement.Namespaces.CreateNamespace;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.ContentManagement.ResourceGroups.CreateResourceGroup;

/// <summary>
/// Query validations. 
/// </summary>
public sealed class CreateResourceGroupCommandValidator : AbstractValidator<CreateNamespaceCommand>
{
    ///<inheritdoc cref="CreateResourceGroupCommandValidator"/>
    public CreateResourceGroupCommandValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.Name)
            .NotNullOrEmpty(localizer, MessageKey.GlobalName);
    }
}