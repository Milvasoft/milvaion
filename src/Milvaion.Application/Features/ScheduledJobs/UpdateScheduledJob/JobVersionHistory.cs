using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Milvaion.Application.Features.ScheduledJobs.UpdateScheduledJob;

/// <summary>
/// Maintains the snapshots kept in <see cref="ScheduledJob.JobVersions"/>.
/// </summary>
/// <remarks>
/// A snapshot must not contain the history itself: each one would then embed every earlier snapshot and the column
/// would roughly double with every update.
/// </remarks>
internal static class JobVersionHistory
{
    private static readonly string[] _excludedProperties = [nameof(ScheduledJob.JobVersions), nameof(ScheduledJob.Occurrences), nameof(ScheduledJob.FailedOccurrences)];

    private static readonly JsonSerializerOptions _snapshotOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { ExcludeHistory } }
    };

    /// <summary>
    /// Returns the history with a snapshot of <paramref name="job"/> appended. Snapshots written before the history
    /// was excluded are compacted on the way through, so rows that have already grown shrink on their next update.
    /// </summary>
    public static List<string> Append(List<string> versions, ScheduledJob job)
    {
        List<string> history = [.. (versions ?? []).Select(Compact)];

        history.Add(JsonSerializer.Serialize(job, _snapshotOptions));

        return history;
    }

    private static string Compact(string snapshot)
    {
        if (string.IsNullOrEmpty(snapshot) || !_excludedProperties.Any(snapshot.Contains))
            return snapshot;

        try
        {
            if (JsonNode.Parse(snapshot) is not JsonObject root)
                return snapshot;

            foreach (var property in _excludedProperties)
                root.Remove(property);

            return root.ToJsonString();
        }
        catch (JsonException)
        {
            // The UI already shows a parse error for a damaged entry; leave it for that rather than losing it.
            return snapshot;
        }
    }

    private static void ExcludeHistory(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type != typeof(ScheduledJob))
            return;

        foreach (var property in typeInfo.Properties.Where(p => _excludedProperties.Contains(p.Name)))
            property.ShouldSerialize = static (_, _) => false;
    }
}
