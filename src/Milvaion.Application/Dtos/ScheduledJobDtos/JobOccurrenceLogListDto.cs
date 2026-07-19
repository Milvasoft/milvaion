namespace Milvaion.Application.Dtos.ScheduledJobDtos;

/// <summary>
/// One log line written during a job execution.
/// </summary>
public class JobOccurrenceLogListDto
{
    /// <summary> Log line identifier. </summary>
    public Guid Id { get; set; }

    /// <summary> Execution this line belongs to. </summary>
    public Guid OccurrenceId { get; set; }

    /// <summary> Job the execution belongs to. </summary>
    public Guid JobId { get; set; }

    /// <summary> Name the worker knows the job by. </summary>
    public string JobName { get; set; }

    /// <summary> When the line was written, UTC. </summary>
    public DateTime Timestamp { get; set; }

    /// <summary> Severity, as the worker reported it - Information, Warning, Error and so on. </summary>
    public string Level { get; set; }

    /// <summary> Grouping label, e.g. Dispatcher. </summary>
    public string Category { get; set; }

    /// <summary> The message text. </summary>
    public string Message { get; set; }

    /// <summary> Exception type name, when the line records one. </summary>
    public string ExceptionType { get; set; }

    /// <summary>
    /// Names of the structured fields attached to this line, without their values.
    /// </summary>
    /// <remarks>
    /// Always present, so a caller can see what was logged and decide whether the values
    /// are worth asking for. The values themselves are withheld unless explicitly requested,
    /// because their content is decided by the worker rather than by Milvaion and can carry
    /// business data.
    /// </remarks>
    public List<string> DataKeys { get; set; } = [];

    /// <summary>
    /// The structured fields with their values. Populated only when explicitly requested.
    /// </summary>
    public Dictionary<string, object> Data { get; set; }
}
