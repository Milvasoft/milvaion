using Cronos;

namespace Milvaion.Application.Utils.Scheduling;

/// <summary>
/// Evaluates six part cron expressions in a job's own time zone, with wall clock semantics across daylight saving
/// time changes.
/// </summary>
/// <remarks>
/// Cronos already converts between the zone and UTC, but it follows Vixie cron on the two DST edges, and that is
/// not what a person reading "every day at 02:30" expects:
/// <list type="bullet">
///   <item>
///     When the clocks go forward, a time inside the gap (02:30 on a night where 02:00 jumps to 03:00) does not
///     exist. Cronos moves such a run to the end of the gap; here it is dropped, because that wall time never
///     happened that day.
///   </item>
///   <item>
///     When the clocks go back, an hour happens twice. Cronos fires interval and range expressions in both copies;
///     here a wall time runs once, at its first occurrence.
///   </item>
/// </list>
/// Every scheduling path - the dispatcher, startup recovery, job create and update, and Upcoming Executions - goes
/// through this class, so the time on screen and the time a job fires cannot drift apart.
/// </remarks>
public static class ZonedCron
{
    /// <summary>
    /// Guards the skip loop. A real expression meets at most a handful of DST edges in a row.
    /// </summary>
    private const int _maxSkips = 1000;

    /// <summary>
    /// Parses a six part (seconds first) cron expression.
    /// </summary>
    public static CronExpression Parse(string cronExpression) => CronExpression.Parse(cronExpression, CronFormat.IncludeSeconds);

    /// <summary>
    /// Tries to parse a six part (seconds first) cron expression.
    /// </summary>
    public static bool TryParse(string cronExpression, out CronExpression expression)
    {
        expression = null;

        return !string.IsNullOrWhiteSpace(cronExpression) && CronExpression.TryParse(cronExpression, CronFormat.IncludeSeconds, out expression);
    }

    /// <summary>
    /// Returns true when <paramref name="timeZoneId"/> is an IANA time zone id this machine knows.
    /// </summary>
    /// <remarks>
    /// Windows ids such as "Turkey Standard Time" are rejected even where the runtime would resolve them: the id is
    /// stored and shown to people, and the API documents IANA ids, so one spelling per zone keeps it unambiguous.
    /// </remarks>
    public static bool IsValidTimeZoneId(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId) || timeZoneId.Length > 64 || timeZoneId.Trim() != timeZoneId)
            return false;

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out _))
            return false;

        return timeZoneId == ScheduledJob.DefaultTimeZoneId || TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZoneId, out _);
    }

    /// <summary>
    /// Resolves a time zone id, falling back to UTC for null, empty or unknown ids.
    /// </summary>
    /// <remarks>
    /// Falling back rather than throwing keeps a job with a zone that was removed from the host's tz database
    /// running on UTC instead of stopping the whole dispatch cycle. Ids are validated on the way in, so this only
    /// matters for data that changed underneath the scheduler.
    /// </remarks>
    public static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId) || timeZoneId == ScheduledJob.DefaultTimeZoneId)
            return TimeZoneInfo.Utc;

        return TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var zone) ? zone : TimeZoneInfo.Utc;
    }

    /// <summary>
    /// Returns the next run strictly after (or, with <paramref name="inclusive"/>, at or after) <paramref name="fromUtc"/>.
    /// </summary>
    /// <param name="expression">Parsed cron expression.</param>
    /// <param name="fromUtc">Start of the search, UTC.</param>
    /// <param name="zone">Zone the expression is written in.</param>
    /// <param name="inclusive">Whether <paramref name="fromUtc"/> itself may be returned.</param>
    /// <returns>The next run in UTC, or null when the expression has no further runs.</returns>
    public static DateTime? GetNextOccurrence(CronExpression expression, DateTime fromUtc, TimeZoneInfo zone, bool inclusive = false)
    {
        zone ??= TimeZoneInfo.Utc;
        fromUtc = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);

        var candidate = expression.GetNextOccurrence(fromUtc, zone, inclusive);

        // UTC has no DST, so nothing to filter.
        if (zone == TimeZoneInfo.Utc || zone.Id == TimeZoneInfo.Utc.Id)
            return candidate;

        for (var i = 0; i < _maxSkips && candidate.HasValue; i++)
        {
            if (!IsShiftedOutOfGap(expression, candidate.Value, zone) && !IsSecondCopyOfRepeatedTime(candidate.Value, zone))
                return candidate;

            candidate = expression.GetNextOccurrence(candidate.Value, zone, inclusive: false);
        }

        return candidate;
    }

    /// <summary>
    /// Convenience overload taking the expression and zone as strings.
    /// </summary>
    public static DateTime? GetNextOccurrence(string cronExpression, DateTime fromUtc, string timeZoneId, bool inclusive = false)
        => GetNextOccurrence(Parse(cronExpression), fromUtc, ResolveTimeZone(timeZoneId), inclusive);

    /// <summary>
    /// Enumerates runs in <c>[fromUtc, toUtc]</c> (start inclusive only when <paramref name="fromInclusive"/>).
    /// </summary>
    public static IEnumerable<DateTime> GetOccurrences(CronExpression expression, DateTime fromUtc, DateTime toUtc, TimeZoneInfo zone, bool fromInclusive = true)
    {
        var next = GetNextOccurrence(expression, fromUtc, zone, fromInclusive);

        while (next.HasValue && next.Value <= toUtc)
        {
            yield return next.Value;

            next = GetNextOccurrence(expression, next.Value, zone);
        }
    }

    /// <summary>
    /// The calendar date of <paramref name="utc"/> as seen on a wall clock in <paramref name="zone"/>.
    /// </summary>
    public static DateOnly ToLocalDate(DateTime utc, TimeZoneInfo zone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone ?? TimeZoneInfo.Utc));

    /// <summary>
    /// The wall clock time of <paramref name="utc"/> in <paramref name="zone"/>.
    /// </summary>
    public static DateTime ToLocalTime(DateTime utc, TimeZoneInfo zone)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone ?? TimeZoneInfo.Utc);

    /// <summary>
    /// True when Cronos moved a run that falls inside a spring-forward gap to the end of the gap.
    /// </summary>
    /// <remarks>
    /// Detected by asking whether the expression matches the candidate's local wall time at all. A run moved out of
    /// the gap lands on a wall time the expression does not name (03:00 for "30 2 * * *"); a genuine run always
    /// lands on one it names. Evaluating the wall time as if it were UTC is safe here because UTC has no gaps.
    /// </remarks>
    private static bool IsShiftedOutOfGap(CronExpression expression, DateTime candidateUtc, TimeZoneInfo zone)
    {
        var wallClock = DateTime.SpecifyKind(ToLocalTime(candidateUtc, zone), DateTimeKind.Utc);

        return expression.GetNextOccurrence(wallClock, TimeZoneInfo.Utc, inclusive: true) != wallClock;
    }

    /// <summary>
    /// True when the candidate is the second time a repeated (fall-back) wall time occurs.
    /// </summary>
    /// <remarks>
    /// During the repeated hour the zone has two offsets; the first pass through it uses the larger (daylight)
    /// offset and the second the smaller (standard) one. The first pass has already run - or lay before the start
    /// of the search, in which case it was the run - so the second is dropped.
    /// </remarks>
    private static bool IsSecondCopyOfRepeatedTime(DateTime candidateUtc, TimeZoneInfo zone)
    {
        var utc = DateTime.SpecifyKind(candidateUtc, DateTimeKind.Utc);

        if (!zone.IsAmbiguousTime(utc))
            return false;

        var offsets = zone.GetAmbiguousTimeOffsets(utc);

        if (offsets.Length < 2)
            return false;

        return zone.GetUtcOffset(utc) == offsets.Min();
    }
}
