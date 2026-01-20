using FluentValidation;

namespace Milvaion.Application.Features.Roles.GetRoleList;

/// <summary>
/// Account detail query validations. 
/// </summary>
public sealed class GetRoleListQueryValidator : AbstractValidator<GetRoleListQuery>
{
    ///<inheritdoc cref="GetRoleListQueryValidator"/>
    public GetRoleListQueryValidator()
    {
    }
}