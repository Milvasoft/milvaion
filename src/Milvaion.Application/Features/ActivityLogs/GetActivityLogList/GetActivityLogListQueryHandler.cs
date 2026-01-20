using Milvaion.Application.Dtos.ActivityLogDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Enums;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.ActivityLogs.GetActivityLogList;

/// <summary>
/// Get all activity logs.
/// </summary>
/// <param name="activityLogRepository"></param>
/// <param name="milvaLocalizer"></param>
public class GetActivityLogListQueryHandler(IMilvaionRepositoryBase<ActivityLog> activityLogRepository, IMilvaLocalizer milvaLocalizer) : IInterceptable, IListQueryHandler<GetActivityLogListQuery, ActivityLogListDto>
{
    private readonly IMilvaionRepositoryBase<ActivityLog> _activityLogRepository = activityLogRepository;
    private readonly IMilvaLocalizer _milvaLocalizer = milvaLocalizer;
    private const string _createPrefix = "Create";
    private const string _updatePrefix = "Update";
    private const string _deletePrefix = "Delete";
    private const string _globalPrefix = "Global.";

    /// <inheritdoc/>
    public async Task<ListResponse<ActivityLogListDto>> Handle(GetActivityLogListQuery request, CancellationToken cancellationToken)
    {
        var sortRequest = request?.Sorting;

        if (sortRequest == null && request != null)
            request.Sorting = new Milvasoft.Components.Rest.Request.SortRequest()
            {
                SortBy = nameof(ActivityLogListDto.ActivityDate),
                Type = SortType.Desc
            };

        var response = await _activityLogRepository.GetAllAsync(request, projection: ActivityLogListDto.Projection, cancellationToken: cancellationToken);

        response.Data.ForEach(log => log.ActivityDescription = GetActivityDescription(log.Activity));

        return response;
    }

    private string GetActivityDescription(UserActivity userActivity)
    {
        var activityName = userActivity.ToString();

        if (activityName.StartsWith(_createPrefix))
            return _milvaLocalizer[MessageKey.UserActivityCreateMessage, $"{_milvaLocalizer[_globalPrefix + activityName[_createPrefix.Length..]]}"];

        if (activityName.StartsWith(_updatePrefix))
            return _milvaLocalizer[MessageKey.UserActivityUpdateMessage, $"{_milvaLocalizer[_globalPrefix + activityName[_updatePrefix.Length..]]}"];

        if (activityName.StartsWith(_deletePrefix))
            return _milvaLocalizer[MessageKey.UserActivityDeleteMessage, $"{_milvaLocalizer[_globalPrefix + activityName[_deletePrefix.Length..]]}"];

        return activityName;
    }
}
