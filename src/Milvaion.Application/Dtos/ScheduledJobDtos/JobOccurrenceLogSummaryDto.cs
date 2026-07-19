namespace Milvaion.Application.Dtos.ScheduledJobDtos;

/// <summary>
/// Aggregated shape of the execution logs over a time window.
/// </summary>
/// <remarks>
/// Exists so a question like "what is going wrong across all jobs" can be answered without
/// reading the log table line by line. Everything here is computed by the database and
/// returned as counts, so the size of the response does not grow with the size of the logs.
/// </remarks>
public class JobOccurrenceLogSummaryDto
{
    /// <summary> Start of the window, UTC. </summary>
    public DateTime Since { get; set; }

    /// <summary> End of the window, UTC. </summary>
    public DateTime Until { get; set; }

    /// <summary> Total lines in the window, after filters. </summary>
    public int TotalCount { get; set; }

    /// <summary> Line counts per severity, largest first. </summary>
    public List<LogCountDto> ByLevel { get; set; } = [];

    /// <summary> Line counts per category, largest first. </summary>
    public List<LogCountDto> ByCategory { get; set; } = [];

    /// <summary> Line counts per exception type, largest first. Only lines that record one. </summary>
    public List<LogCountDto> ByExceptionType { get; set; } = [];

    /// <summary> The jobs producing the most lines in the window. </summary>
    public List<LogJobCountDto> TopJobs { get; set; } = [];

    /// <summary>
    /// The most frequently repeated messages.
    /// </summary>
    /// <remarks>
    /// Grouped on the exact message text. Messages that interpolate a value - an id, a
    /// duration - therefore appear as many distinct entries rather than one, so a low count
    /// here does not prove a problem is rare. Structured logging with the variable part in
    /// the data fields is what would make this exact.
    /// </remarks>
    public List<LogMessageCountDto> TopMessages { get; set; } = [];
}

/// <summary>
/// A label and how many lines carried it.
/// </summary>
public class LogCountDto
{
    /// <summary> The value being counted. Null when the column was empty. </summary>
    public string Value { get; set; }

    /// <summary> How many lines. </summary>
    public int Count { get; set; }
}

/// <summary>
/// Log volume for one job.
/// </summary>
public class LogJobCountDto
{
    /// <summary> Job identifier. </summary>
    public Guid JobId { get; set; }

    /// <summary> Name the worker knows the job by. </summary>
    public string JobName { get; set; }

    /// <summary> Total lines from this job in the window. </summary>
    public int Count { get; set; }

    /// <summary> How many of those were errors. </summary>
    public int ErrorCount { get; set; }
}

/// <summary>
/// A repeated message.
/// </summary>
public class LogMessageCountDto
{
    /// <summary> The message text. </summary>
    public string Message { get; set; }

    /// <summary> Severity it was logged at. </summary>
    public string Level { get; set; }

    /// <summary> How many times it appeared. </summary>
    public int Count { get; set; }

    /// <summary> Most recent appearance, UTC. </summary>
    public DateTime LastSeen { get; set; }
}
