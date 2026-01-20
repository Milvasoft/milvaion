using FluentValidation;

namespace Milvaion.Application.Features.InternalNotifications.GetInternalNotificationList;

/// <summary>
/// Account detail query validations. 
/// </summary>
public sealed class GetInternalNotificationListQueryValidator : AbstractValidator<GetInternalNotificationListQuery>
{
    ///<inheritdoc cref="GetInternalNotificationListQueryValidator"/>
    public GetInternalNotificationListQueryValidator()
    {
    }
}