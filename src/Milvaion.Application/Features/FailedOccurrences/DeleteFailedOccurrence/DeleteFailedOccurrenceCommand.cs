using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Features.FailedOccurrences.DeleteFailedOccurrence;

/// <summary>
/// Data transfer object for failedjob deletion.
/// </summary>
public record DeleteFailedOccurrenceCommand : ICommand<List<Guid>>
{
    /// <summary>
    /// Ids of the failedjobs to be deleted.
    /// </summary>
    public List<Guid> FailedOccurrenceIdList { get; set; }
}
