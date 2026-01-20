using Milvaion.Application.Dtos.ScheduledJobDtos;
using Milvasoft.Components.CQRS.Query;

namespace Milvaion.Application.Features.ScheduledJobs.GetScheduledJobDetail;

/// <summary>
/// Data transfer object for scheduledjob details.
/// </summary>
public record GetScheduledJobDetailQuery : IQuery<ScheduledJobDetailDto>
{
    /// <summary>
    /// ScheduledJob id to access details.
    /// </summary>
    public Guid JobId { get; set; }
}
