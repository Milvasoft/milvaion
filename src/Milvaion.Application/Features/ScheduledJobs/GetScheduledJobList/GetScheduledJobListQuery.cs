using Milvaion.Application.Dtos.ScheduledJobDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Request;

namespace Milvaion.Application.Features.ScheduledJobs.GetScheduledJobList;

/// <summary>
/// Data transfer object for scheduledjob list.
/// </summary>
public record GetScheduledJobListQuery : ListRequest, IListRequestQuery<ScheduledJobListDto>
{
    /// <summary>
    /// Search term to filter jobs.
    /// </summary>
    public string SearchTerm { get; set; }
}