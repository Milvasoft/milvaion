namespace Milvasoft.Milvaion.Sdk.Domain.Enums;

/// <summary>
/// What the scheduler does with runs of a recurring job that were missed while it was down or paused.
/// </summary>
/// <remarks>
/// A run counts as missed when two things are true: the dispatcher reached it later than the misfire grace
/// window (the same rule the misfire alert uses), and the run time falls inside a period in which no
/// dispatcher was polling. A run that is merely late because the job was held back by its concurrency
/// policy or by worker capacity is not missed - it still runs late, as before.
/// One-time jobs are never dropped by this policy: a missed one-time job always runs when the scheduler comes back.
/// </remarks>
public enum MissedRunPolicy
{
    /// <summary>
    /// Missed runs are not executed. One skipped occurrence is recorded for them and the job resumes at its
    /// next scheduled time.
    /// </summary>
    Skip = 0,

    /// <summary>
    /// A single catch-up run is executed for all the missed runs, then the job resumes at its next scheduled time.
    /// </summary>
    RunOnce = 1,

    /// <summary>
    /// Every missed run is executed, one after the other, up to the job's maximum catch-up runs. When more runs
    /// were missed than that maximum, the oldest ones are dropped and only the most recent are executed.
    /// </summary>
    RunAll = 2,
}
