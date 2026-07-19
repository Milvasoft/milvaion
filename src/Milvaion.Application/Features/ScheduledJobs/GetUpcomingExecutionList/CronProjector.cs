using Cronos;

namespace Milvaion.Application.Features.ScheduledJobs.GetUpcomingExecutionList;

/// <summary>
/// Works out when a cron schedule fires next.
/// </summary>
/// <remarks>
/// Only workflows need this. A job's next run is read from Redis, where the
/// dispatcher already committed to a time; a workflow's is not stored anywhere,
/// because the workflow engine re-derives it from the cron expression on every
/// poll and triggers when the derived time has passed.
///
/// The rule below therefore has to match <c>WorkflowEngineService</c> exactly. If
/// it drifts, the screen shows one time and the engine fires at another - the kind
/// of disagreement nobody notices until an overnight job runs an hour late.
/// </remarks>
internal static class CronProjector
{
    /// <summary>
    /// Returns the next fire time for a workflow, or null when the expression cannot be parsed.
    /// </summary>
    /// <remarks>
    /// The result can be in the past, and that is meaningful rather than a bug: it means
    /// the engine has not caught up yet and will trigger the workflow on its next poll.
    /// Clamping it to <paramref name="now"/> would hide a workflow that is running behind.
    /// </remarks>
    /// <param name="cronExpression">Six part cron expression, seconds first.</param>
    /// <param name="lastScheduledRunAt">When the engine last triggered it, if ever.</param>
    /// <param name="now">Current time, UTC.</param>
    /// <param name="pollingIntervalSeconds">Engine poll interval, used for the never-run case.</param>
    public static DateTime? GetNextOccurrence(string cronExpression, DateTime? lastScheduledRunAt, DateTime now, int pollingIntervalSeconds)
    {
        if (string.IsNullOrWhiteSpace(cronExpression))
            return null;

        try
        {
            var expression = CronExpression.Parse(cronExpression, CronFormat.IncludeSeconds);

            // The engine counts from the last trigger. A workflow that has never run gets a
            // window reaching slightly into the past so its first occurrence is not skipped -
            // same fallback the engine uses, for the same reason.
            var fromTime = lastScheduledRunAt ?? now.AddSeconds(-pollingIntervalSeconds * 2);

            return expression.GetNextOccurrence(fromTime, TimeZoneInfo.Utc);
        }
        catch (CronFormatException)
        {
            return null;
        }
    }
}
