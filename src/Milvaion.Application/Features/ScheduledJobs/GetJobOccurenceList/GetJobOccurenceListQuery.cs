using Milvaion.Application.Dtos.ScheduledJobDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Request;

namespace Milvaion.Application.Features.ScheduledJobs.GetJobOccurenceList;

/// <summary>
/// Data transfer object for scheduledjob list.
/// </summary>
public record GetJobOccurrenceListQuery : ListRequest, IListRequestQuery<JobOccurrenceListDto>
{
    /// <summary>
    /// Search term to filter job occurrences.
    /// </summary>
    public string SearchTerm { get; set; }
}