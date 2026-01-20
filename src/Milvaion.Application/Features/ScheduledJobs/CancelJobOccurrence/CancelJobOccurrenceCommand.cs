using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Features.ScheduledJobs.CancelJobOccurrence;

/// <summary>
/// Command to cancel a running job occurrence.
/// </summary>
public record CancelJobOccurrenceCommand : ICommand<bool>
{
    /// <summary>
    /// Occurrence ID (CorrelationId) to cancel.
    /// </summary>
    public Guid OccurrenceId { get; set; }

    /// <summary>
    /// Cancellation reason.
    /// </summary>
    public string Reason { get; set; }
}
