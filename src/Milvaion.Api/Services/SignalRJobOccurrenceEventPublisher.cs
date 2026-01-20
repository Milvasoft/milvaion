using Microsoft.AspNetCore.SignalR;
using Milvaion.Api.Hubs;
using Milvaion.Application.Dtos;
using Milvaion.Application.Interfaces;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Utils;

namespace Milvaion.Api.Services;

/// <summary>
/// SignalR implementation of job occurrence event publisher.
/// </summary>
public class SignalRJobOccurrenceEventPublisher(IHubContext<JobsHub> hubContext) : IJobOccurrenceEventPublisher
{
    private readonly IHubContext<JobsHub> _hubContext = hubContext;

#pragma warning disable AsyncFixer01 // Unnecessary async/await usage
    /// <inheritdoc/>
    public async Task PublishLogAddedAsync(Guid occurrenceId, object log, CancellationToken cancellationToken = default)
        => await _hubContext.Clients.Group($"occurrence_{occurrenceId}").SendAsync("OccurrenceLogAdded", new
        {
            occurrenceId,
            log
        }, cancellationToken);

    /// <inheritdoc/>
    public async Task PublishOccurrenceCreatedAsync(List<JobOccurrence> occurrences, IMilvaLogger logger, CancellationToken cancellationToken = default)
    {
        // Fire and forget pattern - collect all publish tasks first
        var publishTasks = new List<Task>(occurrences.Count);

        foreach (var occurrence in occurrences)
        {
            // Fire and forget each event (non-blocking)
            publishTasks.Add(Task.Run(async () =>
            {
                try
                {
                    await PublishOccurrenceCreatedAsync(new OccurrenceCreatedSignal
                    {
                        Id = occurrence.Id,
                        JobId = occurrence.JobId,
                        JobName = occurrence.JobName,
                        Status = (int)occurrence.Status,
                        EndTime = occurrence.EndTime,
                        StartTime = occurrence.StartTime,
                        WorkerId = occurrence.WorkerId,
                        CreatedAt = occurrence.CreatedAt,
                    }, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.Debug(ex, "Failed to publish OccurrenceCreated event for {OccurrenceId}", occurrence.Id);
                }
            }, cancellationToken));
        }

        // Wait for all events with timeout (don't block dispatcher)
        if (publishTasks.Count > 0)
        {
            var signalRTimeout = Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            var signalRCompleted = await Task.WhenAny(Task.WhenAll(publishTasks), signalRTimeout);

            if (signalRCompleted == signalRTimeout)
                logger.Warning("SignalR OccurrenceCreated event publishing timed out after 3 seconds for {Count} events", publishTasks.Count);
        }
    }

    /// <inheritdoc/>
    public async Task PublishOccurrenceUpdatedAsync(List<JobOccurrence> occurrences, IMilvaLogger logger, CancellationToken cancellationToken = default)
    {
        // Fire and forget pattern - collect all publish tasks first
        var publishTasks = new List<Task>(occurrences.Count);

        foreach (var occurrence in occurrences)
        {
            // Fire and forget each event (non-blocking)
            publishTasks.Add(Task.Run(async () =>
            {
                try
                {
                    await PublishOccurrenceUpdatedAsync(new OccurrenceUpdatedSignal
                    {
                        Id = occurrence.Id,
                        Status = (int)occurrence.Status,
                        StartTime = occurrence.StartTime,
                        EndTime = occurrence.EndTime,
                    }, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.Debug(ex, "Failed to publish OccurrenceCreated event for {OccurrenceId}", occurrence.Id);
                }
            }, cancellationToken));
        }

        // Wait for all events with timeout (don't block dispatcher)
        if (publishTasks.Count > 0)
        {
            var signalRTimeout = Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            var signalRCompleted = await Task.WhenAny(Task.WhenAll(publishTasks), signalRTimeout);

            if (signalRCompleted == signalRTimeout)
                logger.Warning("SignalR OccurrenceCreated event publishing timed out after 3 seconds for {Count} events", publishTasks.Count);
        }
    }

    /// <inheritdoc/>
    private async Task PublishOccurrenceCreatedAsync(OccurrenceCreatedSignal signal, CancellationToken cancellationToken = default)
        => await _hubContext.Clients.All.SendAsync("OccurrenceCreated", signal, cancellationToken);

    /// <inheritdoc/>
    private async Task PublishOccurrenceUpdatedAsync(OccurrenceUpdatedSignal signal, CancellationToken cancellationToken = default)
    {
        // Send to specific occurrence subscribers (detail page)
        await _hubContext.Clients.Group($"occurrence_{signal.Id}").SendAsync("OccurrenceUpdated", signal, cancellationToken);

        // Also broadcast to all clients (for list views)
        await _hubContext.Clients.All.SendAsync("OccurrenceUpdated", signal, cancellationToken);

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
