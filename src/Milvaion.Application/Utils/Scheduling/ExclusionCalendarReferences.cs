using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Utils.Scheduling;

/// <summary>
/// Checks the calendar ids a job refers to.
/// </summary>
public static class ExclusionCalendarReferences
{
    /// <summary>
    /// Removes duplicates and empty ids, keeping the caller's order.
    /// </summary>
    public static List<Guid> Normalize(IEnumerable<Guid> ids) => ids is null ? [] : [.. ids.Where(id => id != Guid.Empty).Distinct()];

    /// <summary>
    /// Returns an error message naming the ids that do not exist, or null when all of them do.
    /// </summary>
    public static async Task<string> FindMissingAsync(IMilvaionRepositoryBase<ExclusionCalendar> repository, List<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids is null || ids.Count == 0)
            return null;

        var existing = await repository.GetAllAsync(c => ids.Contains(c.Id),
                                                    projection: c => new ExclusionCalendar { Id = c.Id },
                                                    cancellationToken: cancellationToken);

        var missing = ids.Except(existing.Select(c => c.Id)).ToList();

        return missing.Count == 0 ? null : $"Exclusion calendar not found: {string.Join(", ", missing)}.";
    }
}
