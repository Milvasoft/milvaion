namespace Milvaion.Application.Dtos.UpcomingExecutionDtos;

/// <summary>
/// The upcoming execution timeline plus the context needed to read it.
/// </summary>
public class UpcomingExecutionListDto
{
    /// <summary>
    /// Server time the timeline was built at.
    /// </summary>
    /// <remarks>
    /// Relative labels - "in 4 minutes" - have to be measured against this rather than
    /// the browser clock. A workstation a few minutes off would otherwise show a run
    /// as overdue while the dispatcher considers it early.
    /// </remarks>
    public DateTime AsOfUtc { get; set; }

    /// <summary>
    /// Upcoming runs, earliest first. Entries with no run time are sorted last.
    /// </summary>
    public List<UpcomingExecutionDto> Items { get; set; } = [];

    /// <summary>
    /// How many entries have a schedule but no run time - see <see cref="UpcomingExecutionHealth.NotScheduled"/>.
    /// </summary>
    public int NotScheduledCount { get; set; }

    /// <summary>
    /// Whether the result hit the requested limit, meaning there are more runs beyond it.
    /// </summary>
    public bool HasMore { get; set; }

    /// <summary>
    /// Whether the check for unscheduled entries covered every recurring job.
    /// </summary>
    /// <remarks>
    /// The check reads recurring jobs from the database and asks Redis whether each one
    /// has a run time. That is bounded so a large installation cannot turn a page load
    /// into a full table scan; when the bound is hit this is true and
    /// <see cref="NotScheduledCount"/> is a floor rather than a total.
    /// </remarks>
    public bool HealthScanTruncated { get; set; }

    /// <summary>
    /// Whether the Redis scheduler could be reached.
    /// </summary>
    /// <remarks>
    /// The Redis client fails open through a circuit breaker, so an outage returns an
    /// empty set rather than an error. Without this flag the screen would quietly claim
    /// nothing is scheduled - the same output as a genuinely empty scheduler.
    /// </remarks>
    public bool SchedulerReachable { get; set; } = true;
}
