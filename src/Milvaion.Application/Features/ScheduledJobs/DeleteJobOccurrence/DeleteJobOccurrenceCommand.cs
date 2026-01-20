using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Features.ScheduledJobs.DeleteJobOccurrence;

/// <summary>
/// Data transfer object for job occurrence deletion.
/// </summary>
public record DeleteJobOccurrenceCommand : ICommand<List<Guid>>
{
    /// <summary>
    /// Ids of the job occurrences to be deleted.
    /// </summary>
    public List<Guid> OccurrenceIdList { get; set; }
}
