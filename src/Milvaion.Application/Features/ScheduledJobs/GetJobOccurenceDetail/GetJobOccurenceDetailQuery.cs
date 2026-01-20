using Milvaion.Application.Dtos.ScheduledJobDtos;
using Milvasoft.Components.CQRS.Query;

namespace Milvaion.Application.Features.ScheduledJobs.GetJobOccurenceDetail;

/// <summary>
/// Data transfer object for scheduledjob details.
/// </summary>
public record GetJobOccurrenceDetailQuery : IQuery<JobOccurrenceDetailDto>
{
    /// <summary>
    /// ScheduledJob id to access details.
    /// </summary>
    public Guid OccurrenceId { get; set; }
}
