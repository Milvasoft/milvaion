using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace Milvaion.Api.Hubs;

/// <summary>
/// SignalR hub for real-time job updates to dashboard.
/// </summary>
public class JobsHub : Hub
{
    // Track active groups and their members
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _activeGroups = new();

    /// <summary>
    /// Called when client connects.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("Connected", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Subscribe to specific occurrence updates (for real-time log streaming).
    /// </summary>
    public async Task SubscribeToOccurrence(string occurrenceId)
    {
        var groupName = $"occurrence_{occurrenceId}";
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);

        // Track this group
        _activeGroups.GetOrAdd(groupName, _ => new ConcurrentDictionary<string, byte>())
                     .TryAdd(Context.ConnectionId, 0);
    }

    /// <summary>
    /// Unsubscribe from specific occurrence updates.
    /// </summary>
    public async Task UnsubscribeFromOccurrence(string occurrenceId)
    {
        var groupName = $"occurrence_{occurrenceId}";
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);

        // Remove from tracking
        if (_activeGroups.TryGetValue(groupName, out var members))
        {
            members.TryRemove(Context.ConnectionId, out _);

            // If group is empty, remove it from tracking
            if (members.IsEmpty)
                _activeGroups.TryRemove(groupName, out _);
        }
    }

    /// <summary>
    /// Called when a client disconnects.
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception exception)
    {
        // Remove this connection from all tracked groups
        foreach (var group in _activeGroups)
        {
            if (group.Value.TryRemove(Context.ConnectionId, out _))
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, group.Key);

                // If group is empty after removal, clean it up
                if (group.Value.IsEmpty)
                    _activeGroups.TryRemove(group.Key, out _);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Server-side method to clean up occurrence group when job completes.
    /// Called from SignalRJobOccurrenceEventPublisher.
    /// </summary>
    public static void CleanupOccurrenceGroup(string occurrenceId)
    {
        var groupName = $"occurrence_{occurrenceId}";

        // Remove from tracking - SignalR group itself will be cleaned up by GC eventually
        // This prevents us from tracking millions of completed occurrences
        _activeGroups.TryRemove(groupName, out _);
    }

    /// <summary>
    /// Get count of active tracked groups (for monitoring).
    /// </summary>
    public static int GetActiveGroupCount() => _activeGroups.Count;
}
