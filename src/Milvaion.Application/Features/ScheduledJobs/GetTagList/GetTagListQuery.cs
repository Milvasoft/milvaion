using Milvasoft.Components.CQRS.Query;

namespace Milvaion.Application.Features.ScheduledJobs.GetTagList;

/// <summary>
/// Data transfer object for scheduledjob list.
/// </summary>
public record GetTagListQuery : IQuery<List<string>>
{
}