using FluentValidation;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.Roles.GetRoleDetail;

/// <summary>
/// Account detail query validations. 
/// </summary>
public sealed class GetRoleDetailQueryValidator : AbstractValidator<GetRoleDetailQuery>
{
    ///<inheritdoc cref="GetRoleDetailQueryValidator"/>
    public GetRoleDetailQueryValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.RoleId)
            .GreaterThan(0)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.Role]]);
    }
}