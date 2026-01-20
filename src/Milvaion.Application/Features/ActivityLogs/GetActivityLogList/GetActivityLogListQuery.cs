using Milvaion.Application.Dtos.ActivityLogDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Request;

namespace Milvaion.Application.Features.ActivityLogs.GetActivityLogList;

/// <summary>
/// Data transfer object for user activity log list.
/// </summary>
public record GetActivityLogListQuery : ListRequest, IListRequestQuery<ActivityLogListDto>
{
}
