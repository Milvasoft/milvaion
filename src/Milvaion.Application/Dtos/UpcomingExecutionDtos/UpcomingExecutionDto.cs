namespace Milvaion.Application.Dtos.UpcomingExecutionDtos;

/// <summary>
/// A single upcoming execution - the next run of one job or one workflow.
/// </summary>
/// <remarks>
/// Jobs and workflows are scheduled by two different mechanisms and this type
/// flattens both onto one row so they can share a timeline:
///
/// <list type="bullet">
///   <item>
///     A job's next run lives in the Redis sorted set. That is the live value;
///     the <c>ExecuteAt</c> column holds the originally configured start time and
///     goes stale the moment a recurring job runs once.
///   </item>
///   <item>
///     A workflow's next run is computed from its cron expression, because the
///     workflow engine polls the database rather than using Redis. Nothing has
///     committed to that time yet, which is why it is reported as a projection.
///   </item>
/// </list>
///
/// The distinction matters when reading the screen: a job row is a promise, a
/// workflow row is an estimate.
/// </remarks>
public class UpcomingExecutionDto
{
    /// <summary> Identifier of the job or workflow this run belongs to. </summary>
    public Guid Id { get; set; }

    /// <summary> Whether this row is a job or a workflow. </summary>
    public UpcomingExecutionKind Kind { get; set; }

    /// <summary> Display name of the job or workflow. </summary>
    public string DisplayName { get; set; }

    /// <summary>
    /// When it is expected to run. Null when nothing has it scheduled - see <see cref="Health"/>.
    /// </summary>
    public DateTime? ScheduledAt { get; set; }

    /// <summary> How much to trust <see cref="ScheduledAt"/>. </summary>
    public UpcomingExecutionHealth Health { get; set; }

    /// <summary> Cron expression, or null for a one-time job. </summary>
    public string CronExpression { get; set; }

    /// <summary> Whether this repeats, i.e. whether a cron expression is set. </summary>
    public bool IsRecurring { get; set; }

    /// <summary> Worker that will pick the job up. Empty for workflows. </summary>
    public string WorkerId { get; set; }

    /// <summary> Name the worker knows the job by. Empty for workflows. </summary>
    public string JobNameInWorker { get; set; }

    /// <summary> Comma separated tags. </summary>
    public string Tags { get; set; }

    /// <summary>
    /// Whether the job belongs to an external scheduler such as Quartz or Hangfire.
    /// </summary>
    /// <remarks>
    /// External jobs are never dispatched by Milvaion - they only report what they
    /// already ran. They are absent from the Redis set by design, so their absence
    /// is not a fault.
    /// </remarks>
    public bool IsExternal { get; set; }
}

/// <summary>
/// What kind of thing is going to run.
/// </summary>
public enum UpcomingExecutionKind
{
    /// <summary> A scheduled job. </summary>
    Job = 0,

    /// <summary> A workflow. </summary>
    Workflow = 1
}

/// <summary>
/// How much the scheduled time can be trusted.
/// </summary>
public enum UpcomingExecutionHealth
{
    /// <summary>
    /// The dispatcher holds this time in Redis. It will fire unless something changes.
    /// </summary>
    Scheduled = 0,

    /// <summary>
    /// Computed from the cron expression rather than read from a scheduler.
    /// Applies to workflows, whose engine polls the database instead of Redis.
    /// </summary>
    Projected = 1,

    /// <summary>
    /// Active, has a schedule, but nothing holds a run time for it - so it will not run.
    /// </summary>
    /// <remarks>
    /// For a job this means it is missing from the Redis sorted set: the dispatcher
    /// does not know about it. This is the failure that is otherwise invisible, since
    /// the job list still shows it as active and no occurrence is ever created to fail.
    /// </remarks>
    NotScheduled = 2,

    /// <summary>
    /// The cron expression could not be parsed, so no future run can be derived.
    /// </summary>
    InvalidSchedule = 3
}
