using Microsoft.Extensions.Logging;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Worker.Options;

namespace Milvasoft.Milvaion.Sdk.Worker.Abstractions;

/// <summary>
/// Provides context and utilities for job execution.
/// </summary>
public interface IJobContext
{
    /// <summary>
    /// Unique correlation ID for this job execution (links to JobOccurrence).
    /// </summary>
    Guid CorrelationId { get; }

    /// <summary>
    /// Parent scheduled job definition.
    /// </summary>
    ScheduledJob Job { get; }

    /// <summary>
    /// Worker identifier executing this job.
    /// </summary>
    string WorkerId { get; }

    /// <summary>
    /// Cancellation token for graceful shutdown or job cancellation.
    /// </summary>
    CancellationToken CancellationToken { get; }

    /// <summary>
    /// Logger instance for writing logs (automatically captured and sent to producer).
    /// </summary>
    IMilvaLogger Logger { get; }

    /// <summary>
    /// Executor job consumer configuration.
    /// </summary>
    JobConsumerConfig ExecutorJobConsumerConfig { get; }

    /// <summary>
    /// Deserializes and returns the job data as the specified type.
    /// Uses the Job.JobData JSON string.
    /// </summary>
    /// <typeparam name="T">The type to deserialize to</typeparam>
    /// <returns>Deserialized job data or default if null/empty</returns>
    T GetData<T>() where T : class;

    /// <summary>
    /// Shorthand for logging information without structured data.
    /// </summary>
    void Log(string message);

    /// <summary>
    /// User friendly log. Logs a message with specified level and optional data.
    /// Automatically appended to job occurrence logs.
    /// </summary>
    /// <param name="level">Log level (Information, Warning, Error, etc.)</param>
    /// <param name="message">Log message</param>
    /// <param name="data">Optional structured data</param>
    /// <param name="category">Optional log category</param>
    void Log(LogLevel level, string message, Dictionary<string, object> data = null, string category = "UserCode");

    /// <summary>
    /// User friendly log. Shorthand for logging information.
    /// </summary>
    void LogInformation(string message, Dictionary<string, object> data = null);

    /// <summary>
    /// User friendly log. Shorthand for logging warnings.
    /// </summary>
    void LogWarning(string message, Dictionary<string, object> data = null);

    /// <summary>
    /// User friendly log. Shorthand for logging errors.
    /// </summary>
    void LogError(string message, Exception ex = null, Dictionary<string, object> data = null);

    /// <summary>
    /// Gets all logs collected during job execution.
    /// </summary>
    List<OccurrenceLog> GetLogs();
}
