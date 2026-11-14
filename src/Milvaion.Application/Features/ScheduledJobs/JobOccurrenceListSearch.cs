using Microsoft.EntityFrameworkCore;
using Milvaion.Application.Interfaces.Redis;
using Milvasoft.Components.Rest.Enums;
using Milvasoft.Components.Rest.Request;
using System.Linq.Expressions;
using System.Text.Json;

namespace Milvaion.Application.Features.ScheduledJobs;

/// <summary>
/// Search and count logic shared by the paged and cursor based occurrence lists.
/// </summary>
internal static class JobOccurrenceListSearch
{
    /// <summary>
    /// Builds the search predicate: an exact id match for a guid, otherwise a job name search.
    /// </summary>
    public static Expression<Func<JobOccurrence, bool>> BuildPredicate(string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
            return _ => true;

        if (Guid.TryParse(searchTerm, out var guid))
            return c => c.Id == guid;

        var pattern = searchTerm.ToContainsPattern();

        return c => EF.Functions.ILike(c.JobName, pattern, MilvaionExtensions.LikeEscapeCharacter);
    }

    /// <summary>
    /// Returns the total from the Redis counters when they answer exactly this query, or null when the database has
    /// to count. The counters only know the grand total and a total per status.
    /// </summary>
    /// <remarks>
    /// The counters cover every project, so they are used only while the project filter is bypassed (projects disabled).
    /// </remarks>
    public static async Task<int?> GetCountFromStatsAsync(FilterRequest filtering, string searchTerm, IRedisStatsService statsService, CancellationToken cancellationToken, IProjectContext projectContext = null)
    {
        if (!string.IsNullOrWhiteSpace(searchTerm) || projectContext?.BypassFilter == false)
            return null;

        var criterias = filtering?.Criterias ?? [];

        string counterName;

        if (criterias.Count == 0)
            counterName = "Total";
        else if (criterias.Count == 1 && criterias[0].FilterBy == nameof(JobOccurrence.Status) && criterias[0].Type == FilterType.EqualTo)
            counterName = Enum.GetName(ConvertValue<JobOccurrenceStatus>(criterias[0].Value));
        else
            return null;

        if (counterName is null)
            return null;

        var stats = await statsService.GetStatisticsAsync(cancellationToken);

        return stats.GetValueOrDefault(counterName, 0).ToSaturatedInt();
    }

    private static TActualType ConvertValue<TActualType>(object value)
    {
        try
        {
            if (value is TActualType typedValue)
                return typedValue;

            if (value is JsonElement jsonElement)
                return jsonElement.Deserialize<TActualType>();

            return (TActualType)Convert.ChangeType(value, typeof(TActualType));
        }
        catch
        {
            return default;
        }
    }
}
