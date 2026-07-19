using Cronos;

namespace Milvaion.Infrastructure.Scheduling;

/// <summary>
/// Decides what run time startup recovery should write to Redis for a job.
/// </summary>
/// <remarks>
/// Recovery used to take the <c>ExecuteAt</c> column as the answer. That column is
/// only written when a job is created or updated: once a recurring job has run,
/// <c>HandleRecurringJobAsync</c> advances the schedule in memory and pushes it to
/// Redis, and nothing writes it back to the database. So for any job that has been
/// running for a while the column sits in the past.
///
/// Feeding that back into the sorted set on every restart put every recurring job
/// at a past score, and the next dispatcher poll saw all of them as due at once -
/// which is why a deploy fired the whole catalogue.
///
/// The rule now: Redis holds the live schedule and recovery does not second-guess
/// it. Recovery only supplies a time when Redis has none, and for a recurring job
/// it derives that time the same way the dispatcher does rather than reading a
/// column that cannot be current.
/// </remarks>
internal static class StartupScheduleResolver
{
    /// <summary>
    /// Returns the time recovery should write, or null to leave the schedule untouched.
    /// </summary>
    /// <param name="cronExpression">Six part cron expression, or null for a one-time job.</param>
    /// <param name="executeAt">The <c>ExecuteAt</c> column.</param>
    /// <param name="completedAt">When a one-time job was dispatched, or null.</param>
    /// <param name="existingScheduleTime">Current sorted set score, or null when absent.</param>
    /// <param name="now">Current time, UTC.</param>
    public static DateTime? Resolve(string cronExpression, DateTime executeAt, DateTime? completedAt, DateTime? existingScheduleTime, DateTime now)
    {
        // Redis already has a time. It came either from the dispatcher advancing the
        // schedule or from an update writing it directly, and both are newer than
        // anything recovery can work out. Leave it alone.
        if (existingScheduleTime.HasValue)
            return null;

        // A one-time job that has already run is finished, and this is the durable
        // record of that. Redis used to carry the fact by omission, which a flush
        // erased - and then recovery scheduled the job again and it ran twice.
        if (completedAt.HasValue)
            return null;

        // One-time job that has not run: the column is the whole schedule and it never
        // goes stale, because such a job is never rescheduled.
        if (string.IsNullOrWhiteSpace(cronExpression))
            return executeAt;

        try
        {
            var expression = CronExpression.Parse(cronExpression, CronFormat.IncludeSeconds);

            // Same call the dispatcher makes when it reschedules, so a job recovered
            // here lands on exactly the boundary it would have landed on anyway.
            return expression.GetNextOccurrence(now, TimeZoneInfo.Utc);
        }
        catch (CronFormatException)
        {
            // Nothing sensible to schedule. Returning null keeps the job out of the
            // sorted set; the dispatcher already prunes invalid cron jobs when it
            // meets them.
            return null;
        }
    }
}
