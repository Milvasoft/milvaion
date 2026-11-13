namespace Milvaion.Application.Utils.Scheduling;

/// <summary>
/// The single definition of a misfire, shared by the misfire alert and the missed-run (catch-up) policy.
/// </summary>
/// <remarks>
/// A due job sits in the Redis scheduled set with its intended run time as the score, and the dispatcher clears it
/// within a poll or two. A run that is still waiting more than <see cref="Grace"/> after its time was not
/// dispatched on time: that is a misfire. <c>JobMisfireMonitorService</c> alerts on it, and the dispatcher uses the
/// same test to decide whether a run it is about to send was missed. Keeping one definition means a run the alert
/// called missed is exactly a run the catch-up policy treats as missed.
/// </remarks>
public static class MisfireRules
{
    /// <summary>
    /// How late a run must be before it counts as a misfire.
    /// </summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);

    /// <summary>
    /// True when a run scheduled at <paramref name="scheduledAtUtc"/> is still undispatched past the grace window.
    /// </summary>
    public static bool IsMisfired(DateTime scheduledAtUtc, DateTime nowUtc) => nowUtc - scheduledAtUtc > Grace;

    /// <summary>
    /// The latest scheduled time that is a misfire at <paramref name="nowUtc"/>.
    /// </summary>
    public static DateTime MisfireCutoff(DateTime nowUtc) => nowUtc - Grace;
}

/// <summary>
/// A period in which no dispatcher was polling - the scheduler was down, restarting or paused by the emergency stop.
/// </summary>
/// <param name="StartUtc">Last poll before the gap.</param>
/// <param name="EndUtc">First poll after the gap.</param>
public sealed record DispatcherOutage(DateTime StartUtc, DateTime EndUtc)
{
    /// <summary>
    /// True when <paramref name="utc"/> fell inside the outage, after the last poll and up to the first poll back.
    /// </summary>
    public bool Covers(DateTime utc) => utc > StartUtc && utc <= EndUtc;
}
