using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Interfaces;

/// <summary>
/// Publishes real-time events for job occurrence updates via SignalR.
/// </summary>
public interface IJobOccurrenceEventPublisher
{
    /// <summary>
    /// Publishes a log added event for a specific occurrence.
    /// </summary>
    Task PublishLogAddedAsync(Guid occurrenceId, object log, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes an occurrence created event to all clients.
    /// </summary>
    Task PublishOccurrenceCreatedAsync(List<JobOccurrence> occurrences, IMilvaLogger logger, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes an occurrence updated event to specific occurrence subscribers and all clients.
    /// Also handles group cleanup when occurrence reaches final state.
    /// </summary>
    Task PublishOccurrenceUpdatedAsync(List<JobOccurrence> occurrences, IMilvaLogger logger, CancellationToken cancellationToken = default);
}
