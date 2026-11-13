using Cronos;

namespace Milvaion.Application.Utils.Scheduling;

/// <summary>
/// What the dispatcher does with a due run.
/// </summary>
public enum RunDecisionKind
{
    /// <summary>Dispatch it as a normal run.</summary>
    Run,

    /// <summary>Dispatch it as a catch-up run for runs missed during an outage.</summary>
    CatchUp,

    /// <summary>Do not dispatch: the run was missed and the job's policy is to skip missed runs.</summary>
    SkipMissed,

    /// <summary>Do not dispatch: the run falls on a day excluded by one of the job's calendars.</summary>
    SkipExcluded,

    /// <summary>The cron expression cannot be parsed.</summary>
    InvalidSchedule,
}

/// <summary>
/// The dispatcher's decision for one due run, and where the job's schedule moves afterwards.
/// </summary>
public sealed class RunDecision
{
    /// <summary>What to do with the run.</summary>
    public RunDecisionKind Kind { get; init; }

    /// <summary>The scheduled time this decision is about (UTC).</summary>
    public DateTime RunSlotUtc { get; init; }

    /// <summary>The next score to write to the scheduled set, or null when the job leaves it.</summary>
    public DateTime? NextRunAtUtc { get; init; }

    /// <summary>Number of runs missed during the outage, counting this one. Zero when nothing was missed.</summary>
    public int MissedCount { get; init; }

    /// <summary>Missed runs dropped because they exceeded the maximum number of catch-up runs.</summary>
    public int DroppedCount { get; init; }

    /// <summary>Earliest missed run (UTC).</summary>
    public DateTime? FirstMissedUtc { get; init; }

    /// <summary>Latest missed run (UTC).</summary>
    public DateTime? LastMissedUtc { get; init; }

    /// <summary>The calendar that excluded the run, for <see cref="RunDecisionKind.SkipExcluded"/>.</summary>
    public ExclusionCalendar ExcludedBy { get; init; }

    /// <summary>Local date of the run in the job's time zone.</summary>
    public DateOnly LocalDate { get; init; }

    /// <summary>True when the run is dispatched to a worker.</summary>
    public bool Dispatches => Kind is RunDecisionKind.Run or RunDecisionKind.CatchUp;

    /// <summary>A one-line explanation for the occurrence log.</summary>
    public string Message { get; init; }
}

/// <summary>
/// Decides, for a job whose run is due, whether to dispatch it, skip it, or run it as a catch-up - and where its
/// schedule goes next. Pure logic: no I/O, so every rule here is unit tested directly.
/// </summary>
/// <remarks>
/// Order of the rules:
/// <list type="number">
///   <item>Missed-run policy, for recurring jobs whose run was missed during a dispatcher outage.</item>
///   <item>Exclusion calendars, for whatever run survives the first step.</item>
/// </list>
/// The schedule always advances through plain cron runs, never straight past excluded days. That is what lets an
/// excluded run be reached and recorded as skipped rather than silently vanishing. When a run is skipped by a
/// calendar the schedule then jumps to the first run on a day that is not excluded, so a calendar that excludes a
/// whole weekend records one skipped occurrence, not one per run.
/// </remarks>
public static class RunPlanner
{
    /// <summary>Bounds the count of missed runs, so an every-second job after a long outage cannot stall a cycle.</summary>
    private const int _maxMissedRunsToEnumerate = 100_000;

    /// <summary>Bounds the search for a non-excluded day (ten years of days).</summary>
    private const int _maxExcludedDaysToSkip = 3660;

    /// <summary>
    /// Plans one due run.
    /// </summary>
    /// <param name="job">The job. <see cref="ScheduledJob.ExecuteAt"/> must be the due score from Redis.</param>
    /// <param name="nowUtc">Current time.</param>
    /// <param name="outage">The most recent dispatcher outage, or null when none is known.</param>
    /// <param name="calendars">The job's exclusion calendars; may be empty.</param>
    public static RunDecision Plan(ScheduledJob job, DateTime nowUtc, DispatcherOutage outage, IReadOnlyCollection<ExclusionCalendar> calendars)
    {
        var zone = ZonedCron.ResolveTimeZone(job.TimeZoneId);
        var scheduledAt = DateTime.SpecifyKind(job.ExecuteAt, DateTimeKind.Utc);

        // One-time job: no catch-up policy (dropping it would lose the only run it has), only the calendar.
        if (string.IsNullOrWhiteSpace(job.CronExpression))
        {
            var oneTimeDate = ZonedCron.ToLocalDate(scheduledAt, zone);
            var oneTimeCalendar = ExclusionCalendar.FindExcluding(calendars, oneTimeDate);

            return new RunDecision
            {
                Kind = oneTimeCalendar is null ? RunDecisionKind.Run : RunDecisionKind.SkipExcluded,
                RunSlotUtc = scheduledAt,
                NextRunAtUtc = null,
                ExcludedBy = oneTimeCalendar,
                LocalDate = oneTimeDate,
                Message = oneTimeCalendar is null ? null : ExcludedMessage(oneTimeDate, oneTimeCalendar, null, zone)
            };
        }

        if (!ZonedCron.TryParse(job.CronExpression, out var cron))
            return new RunDecision { Kind = RunDecisionKind.InvalidSchedule, RunSlotUtc = scheduledAt };

        var missed = outage is not null && MisfireRules.IsMisfired(scheduledAt, nowUtc) && outage.Covers(scheduledAt);

        RunDecisionKind kind;
        DateTime runSlot = scheduledAt;
        DateTime? next;
        int missedCount = 0, droppedCount = 0;
        DateTime? firstMissed = null, lastMissed = null;

        if (!missed)
        {
            kind = RunDecisionKind.Run;
            next = ZonedCron.GetNextOccurrence(cron, nowUtc, zone);
        }
        else
        {
            var maxCatchUp = Math.Max(1, job.MaxCatchUpRuns);
            var (count, last, recent) = CountMissedRuns(cron, zone, scheduledAt, nowUtc, maxCatchUp);

            missedCount = count;
            firstMissed = scheduledAt;
            lastMissed = last;

            switch (job.MissedRunPolicy)
            {
                case MissedRunPolicy.RunOnce:
                    kind = RunDecisionKind.CatchUp;
                    next = ZonedCron.GetNextOccurrence(cron, nowUtc, zone);
                    break;

                case MissedRunPolicy.RunAll:
                    kind = RunDecisionKind.CatchUp;

                    // Too many missed: keep only the most recent maxCatchUp of them and drop the older ones.
                    if (count > maxCatchUp)
                    {
                        droppedCount = count - maxCatchUp;
                        runSlot = recent.Peek();
                    }

                    // Step one run at a time. The next run is usually still in the past, so the dispatcher meets it
                    // again on its next poll and this plan runs again for it - which keeps every catch-up subject to
                    // the job's concurrency policy and its worker's capacity, like any other run.
                    next = ZonedCron.GetNextOccurrence(cron, runSlot, zone);
                    break;

                default:
                    kind = RunDecisionKind.SkipMissed;
                    next = ZonedCron.GetNextOccurrence(cron, nowUtc, zone);
                    break;
            }
        }

        var localDate = ZonedCron.ToLocalDate(runSlot, zone);

        if (kind != RunDecisionKind.SkipMissed)
        {
            var calendar = ExclusionCalendar.FindExcluding(calendars, localDate);

            if (calendar is not null)
            {
                // Jump over the excluded stretch. Measured from the later of the run and its planned successor, so a
                // RunAll catch-up keeps stepping through missed runs instead of jumping to the present.
                var from = next.HasValue && next.Value > runSlot ? next.Value : runSlot;
                var inclusive = next.HasValue && next.Value > runSlot;
                var allowed = GetNextAllowedOccurrence(cron, from, zone, calendars, inclusive);

                return new RunDecision
                {
                    Kind = RunDecisionKind.SkipExcluded,
                    RunSlotUtc = runSlot,
                    NextRunAtUtc = allowed,
                    MissedCount = missedCount,
                    DroppedCount = droppedCount,
                    FirstMissedUtc = firstMissed,
                    LastMissedUtc = lastMissed,
                    ExcludedBy = calendar,
                    LocalDate = localDate,
                    Message = ExcludedMessage(localDate, calendar, allowed, zone)
                };
            }
        }

        return new RunDecision
        {
            Kind = kind,
            RunSlotUtc = runSlot,
            NextRunAtUtc = next,
            MissedCount = missedCount,
            DroppedCount = droppedCount,
            FirstMissedUtc = firstMissed,
            LastMissedUtc = lastMissed,
            LocalDate = localDate,
            Message = kind switch
            {
                RunDecisionKind.CatchUp => CatchUpMessage(job.MissedRunPolicy, scheduledAt, lastMissed, runSlot, missedCount, droppedCount, job.MaxCatchUpRuns),
                RunDecisionKind.SkipMissed => $"Skipped {Runs(missedCount)} missed while the scheduler was down "
                                              + $"({Format(firstMissed)} to {Format(lastMissed)} UTC). Missed-run policy: Skip. Next run: {Format(next)} UTC.",
                _ => null
            }
        };
    }

    /// <summary>
    /// First run at or after (<paramref name="inclusive"/>) <paramref name="fromUtc"/> whose local date no calendar excludes.
    /// </summary>
    /// <returns>The run in UTC, or null when there is none within the search bound.</returns>
    public static DateTime? GetNextAllowedOccurrence(CronExpression cron, DateTime fromUtc, TimeZoneInfo zone, IReadOnlyCollection<ExclusionCalendar> calendars, bool inclusive = false)
    {
        var candidate = ZonedCron.GetNextOccurrence(cron, fromUtc, zone, inclusive);

        if (calendars is null || calendars.Count == 0)
            return candidate;

        for (var i = 0; i < _maxExcludedDaysToSkip && candidate.HasValue; i++)
        {
            var date = ZonedCron.ToLocalDate(candidate.Value, zone);

            if (ExclusionCalendar.FindExcluding(calendars, date) is null)
                return candidate;

            // Every run that day is excluded - restart the search at the next local midnight instead of walking
            // through them one by one.
            candidate = ZonedCron.GetNextOccurrence(cron, StartOfLocalDay(date.AddDays(1), zone), zone, inclusive: true);
        }

        return null;
    }

    /// <summary>
    /// UTC instant at which <paramref name="date"/> begins in <paramref name="zone"/>.
    /// </summary>
    /// <remarks>A few zones start DST at midnight, so 00:00 can be missing; the day then begins at the first valid minute.</remarks>
    public static DateTime StartOfLocalDay(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

        for (var i = 0; i < 240 && zone.IsInvalidTime(local); i++)
            local = local.AddMinutes(1);

        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    /// <summary>
    /// Counts the runs in [first, now], returning the count, the latest one and the most recent <paramref name="keep"/>.
    /// </summary>
    private static (int Count, DateTime Last, Queue<DateTime> Recent) CountMissedRuns(CronExpression cron, TimeZoneInfo zone, DateTime first, DateTime nowUtc, int keep)
    {
        var recent = new Queue<DateTime>(keep + 1);
        var count = 0;
        DateTime last = first;
        DateTime? slot = first;

        while (slot.HasValue && slot.Value <= nowUtc && count < _maxMissedRunsToEnumerate)
        {
            count++;
            last = slot.Value;

            recent.Enqueue(slot.Value);

            if (recent.Count > keep)
                recent.Dequeue();

            slot = ZonedCron.GetNextOccurrence(cron, slot.Value, zone);
        }

        return (count, last, recent);
    }

    private static string CatchUpMessage(MissedRunPolicy policy, DateTime firstMissed, DateTime? lastMissed, DateTime runSlot, int missedCount, int droppedCount, int maxCatchUpRuns)
    {
        if (policy == MissedRunPolicy.RunOnce)
            return $"Catch-up run: {Runs(missedCount)} missed while the scheduler was down ({Format(firstMissed)} to {Format(lastMissed)} UTC). "
                   + "Missed-run policy: RunOnce, so this single run stands in for all of them.";

        var text = $"Catch-up run for the missed run at {Format(runSlot)} UTC ({Runs(Math.Min(missedCount, maxCatchUpRuns))} left to catch up, "
                   + $"missed between {Format(firstMissed)} and {Format(lastMissed)} UTC). Missed-run policy: RunAll.";

        if (droppedCount > 0)
            text += $" {Runs(droppedCount)} older missed were dropped because they exceed the maximum of {maxCatchUpRuns} catch-up runs.";

        return text;
    }

    private static string ExcludedMessage(DateOnly date, ExclusionCalendar calendar, DateTime? next, TimeZoneInfo zone)
    {
        var text = $"Skipped: {date:yyyy-MM-dd} ({zone.Id}) is excluded by calendar '{calendar.Name}'.";

        if (next.HasValue)
            text += $" Next run: {Format(next)} UTC.";

        return text;
    }

    private static string Runs(int count) => count == 1 ? "1 run" : $"{count} runs";

    private static string Format(DateTime? utc) => utc?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
}
