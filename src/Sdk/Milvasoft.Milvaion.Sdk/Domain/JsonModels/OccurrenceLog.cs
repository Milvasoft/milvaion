namespace Milvasoft.Milvaion.Sdk.Domain.JsonModels;

/// <summary>
/// Represents a single log entry from a job execution.
/// Stored in JobOccurrence.Logs as JSONB array.
/// </summary>
public class OccurrenceLog
{
    /// <summary>
    /// Timestamp when the log entry was created (UTC).
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Log level (e.g., "Information", "Warning", "Error", "Debug").
    /// </summary>
    public string Level { get; set; }

    /// <summary>
    /// Log message content.
    /// </summary>
    public string Message { get; set; }

    /// <summary>
    /// Additional structured data associated with the log entry.
    /// Can contain exception details, context information, etc.
    /// </summary>
    public Dictionary<string, object> Data { get; set; }

    /// <summary>
    /// Optional category/source of the log (e.g., "JobExecutor", "UserCode").
    /// </summary>
    public string Category { get; set; }

    /// <summary>
    /// Optional exception type name if this log represents an error.
    /// </summary>
    public string ExceptionType { get; set; }
}
