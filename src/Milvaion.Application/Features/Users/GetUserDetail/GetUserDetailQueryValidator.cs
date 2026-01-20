using FluentValidation;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.Users.GetUserDetail;

/// <summary>
/// Account detail query validations. 
/// </summary>
public sealed class GetUserDetailQueryValidator : AbstractValidator<GetUserDetailQuery>
{
    ///<inheritdoc cref="GetUserDetailQueryValidator"/>
    public GetUserDetailQueryValidator(IMilvaLocalizer localizer)
    {
        RuleFor(query => query.UserId)
            .GreaterThan(0)
            .WithMessage(localizer[MessageKey.PleaseSendCorrect, localizer[MessageKey.User]]);
    }
}