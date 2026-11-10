using Microsoft.AspNetCore.SignalR;
using Milvaion.Api.Hubs;
using Milvaion.Application.Dtos;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Utils;

namespace Milvaion.Api.Services;

/// <summary>
/// SignalR implementation of job occurrence event publisher.
/// </summary>
public class SignalRJobOccurrenceEventPublisher(IHubContext<JobsHub> hubContext) : IJobOccurrenceEventPublisher
{
    private readonly IHubContext<JobsHub> _hubContext = hubContext;

    private const int _maxConcurrentPublishes = 32;
    private static readonly TimeSpan _publishTimeout = TimeSpan.FromSeconds(3);

#pragma warning disable AsyncFixer01 // Unnecessary async/await usage
    /// <inheritdoc/>
    public async Task PublishLogAddedAsync(Guid occurrenceId, object log, CancellationToken cancellationToken = default)
        => await _hubContext.Clients.Group($"occurrence_{occurrenceId}").SendAsync("OccurrenceLogAdded", new
        {
            occurrenceId,
            log
        }, cancellationToken);

    /// <inheritdoc/>
    public Task PublishOccurrenceCreatedAsync(List<JobOccurrence> occurrences, IMilvaLogger logger, CancellationToken cancellationToken = default)
        => PublishAllAsync(occurrences, "OccurrenceCreated", (occurrence, token) => PublishOccurrenceCreatedAsync(new OccurrenceCreatedSignal
        {
            Id = occurrence.Id,
            JobId = occurrence.JobId,
            JobName = occurrence.JobName,
            Status = (int)occurrence.Status,
            EndTime = occurrence.EndTime,
            StartTime = occurrence.StartTime,
            WorkerId = occurrence.WorkerId,
            CreatedAt = occurrence.CreatedAt,
        }, token), logger, cancellationToken);

    /// <inheritdoc/>
    public Task PublishOccurrenceUpdatedAsync(List<JobOccurrence> occurrences, IMilvaLogger logger, CancellationToken cancellationToken = default)
        => PublishAllAsync(occurrences, "OccurrenceUpdated", (occurrence, token) => PublishOccurrenceUpdatedAsync(new OccurrenceUpdatedSignal
        {
            Id = occurrence.Id,
            Status = (int)occurrence.Status,
            StartTime = occurrence.StartTime,
            WorkerId = occurrence.WorkerId,
            EndTime = occurrence.EndTime,
            DurationMs = occurrence.DurationMs,
            Exception = occurrence.Exception,
            StepStatus = occurrence.StepStatus.HasValue ? (int)occurrence.StepStatus.Value : null,
        }, token), logger, cancellationToken);

    /// <summary>
    /// Publishes one event per occurrence with bounded concurrency and gives up after the timeout, cancelling what
    /// is left so a slow client cannot hold up the dispatcher or leave work running behind it.
    /// </summary>
    private static async Task PublishAllAsync(List<JobOccurrence> occurrences, string eventName, Func<JobOccurrence, CancellationToken, Task> publish, IMilvaLogger logger, CancellationToken cancellationToken)
    {
        if (occurrences.Count == 0)
            return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_publishTimeout);

        try
        {
            await Parallel.ForEachAsync(occurrences, new ParallelOptions { MaxDegreeOfParallelism = _maxConcurrentPublishes, CancellationToken = timeout.Token }, async (occurrence, token) =>
            {
                try
                {
                    await publish(occurrence, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.Debug(ex, "Failed to publish {EventName} event for {OccurrenceId}", eventName, occurrence.Id);
                }
            });
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            logger.Warning("SignalR {EventName} event publishing timed out after {Timeout} for {Count} events", eventName, _publishTimeout, occurrences.Count);
        }
    }

    /// <inheritdoc/>
    private async Task PublishOccurrenceCreatedAsync(OccurrenceCreatedSignal signal, CancellationToken cancellationToken = default)
        => await _hubContext.Clients.Group(JobsHub.OccurrenceReadersGroup).SendAsync("OccurrenceCreated", signal, cancellationToken);

    /// <inheritdoc/>
    private async Task PublishOccurrenceUpdatedAsync(OccurrenceUpdatedSignal signal, CancellationToken cancellationToken = default)
    {
        // Send to specific occurrence subscribers (detail page)
        await _hubContext.Clients.Group($"occurrence_{signal.Id}").SendAsync("OccurrenceUpdated", signal, cancellationToken);

        // Also broadcast to everyone allowed to see occurrences (for list views)
        await _hubContext.Clients.Group(JobsHub.OccurrenceReadersGroup).SendAsync("OccurrenceUpdated", signal, cancellationToken);

        // Cleanup group if occurrence is in final state
        if (((JobOccurrenceStatus)signal.Status).IsFinalStatus())
        {
            // Clean up the group from tracking (after a short delay to ensure all messages are delivered)
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5)); // Give time for messages to be delivered
                JobsHub.CleanupOccurrenceGroup(signal.Id.ToString());
            }, cancellationToken);
        }
    }
}
#pragma warning restore AsyncFixer01 // Unnecessary async/await usage
