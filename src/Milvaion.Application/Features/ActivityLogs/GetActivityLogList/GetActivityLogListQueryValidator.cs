using FluentValidation;

namespace Milvaion.Application.Features.ActivityLogs.GetActivityLogList;

/// <summary>
/// Account detail query validations. 
/// </summary>
public sealed class GetActivityLogListQueryValidator : AbstractValidator<GetActivityLogListQuery>
{
    ///<inheritdoc cref="GetActivityLogListQueryValidator"/>
    public GetActivityLogListQueryValidator()
    {
    }
}