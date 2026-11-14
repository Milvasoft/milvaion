using Milvasoft.Milvaion.Sdk.Domain.Enums;

namespace Milvasoft.Milvaion.Sdk.Domain.JsonModels;

/// <summary>
/// Message model for worker log entries sent to producer via RabbitMQ.
/// Published to worker_logs_queue.
/// </summary>
public class WorkerLogMessage
{
    /// <summary>
    /// Unique identifier for the specific job occurrence this log belongs to.
    /// This is the Occurrence.Id from the database.
    /// </summary>
    public Guid OccurrenceId { get; set; }

    /// <summary>
    /// Worker identifier that generated this log.
    /// </summary>
    public string WorkerId { get; set; }

    /// <summary>
    /// Log entry details.
    /// </summary>
    public OccurrenceLog Log { get; set; }

    /// <summary>
    /// Timestamp when this message was created (UTC).
    /// </summary>
    public DateTime MessageTimestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Batch message model for multiple worker logs sent in a single RabbitMQ message.
/// Used for high-throughput scenarios to reduce message overhead.
/// </summary>
public class WorkerLogBatchMessage
{
    /// <summary>
    /// Array of log messages in this batch.
    /// </summary>
    public List<WorkerLogMessage> Logs { get; set; } = [];

    /// <summary>
    /// Timestamp when this batch was created (UTC).
    /// </summary>
    public DateTime BatchTimestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Number of logs in this batch.
    /// </summary>
    public int Count => Logs?.Count ?? 0;
}

/// <summary>
/// Message model for job status updates sent from worker to producer via RabbitMQ.
/// Published to job_status_updates_queue.
/// </summary>
public class JobStatusUpdateMessage
{
    /// <summary>
    /// Unique identifier for the specific job occurrence.
    /// This is the Occurrence.Id from the database.
    /// </summary>
    public Guid OccurrenceId { get; set; }

    /// <summary>
    /// Job ID (parent ScheduledJob).
    /// </summary>
    public Guid JobId { get; set; }

    /// <summary>
    /// Worker identifier (e.g., "test-worker"). Used for worker-level tracking.
    /// </summary>
    public string WorkerId { get; set; }

    /// <summary>
    /// Instance identifier (e.g., "test-worker-bf06453b"). Used for instance-level job counts.
    /// </summary>
    public string InstanceId { get; set; }

    /// <summary>
    /// New status of the job occurrence.
    /// </summary>
    public JobOccurrenceStatus Status { get; set; }

    /// <summary>
    /// Start time (if status is Running).
    /// </summary>
    public DateTime? StartTime { get; set; }

    /// <summary>
    /// End time (if status is Completed/Failed/Cancelled).
    /// </summary>
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// Execution duration in milliseconds.
    /// </summary>
    public long? DurationMs { get; set; }

    /// <summary>
    /// Result message (for Completed status).
    /// </summary>
    public string Result { get; set; }

    /// <summary>
    /// Exception details (for Failed status).
    /// </summary>
    public string Exception { get; set; }

    /// <summary>
    /// Time (UTC) the job asked to be run again at, when it failed with a retry-after error.
    /// Workflow steps are rerun at this time; standalone jobs ignore it.
    /// </summary>
    public DateTime? RetryAfter { get; set; }

    /// <summary>
    /// Timestamp when this message was created (UTC).
    /// </summary>
    public DateTime MessageTimestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Message for registering external jobs (Quartz, Hangfire, etc.) to Milvaion for monitoring.
/// Published to external_job_registration queue.
/// </summary>
public class ExternalJobRegistrationMessage
{
    /// <summary>
    /// External job identifier (e.g., "DEFAULT.MyQuartzJob").
    /// </summary>
    public string ExternalJobId { get; set; }

    /// <summary>
    /// Display name for the job.
    /// </summary>
    public string DisplayName { get; set; }

    /// <summary>
    /// Job description.
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// Job type name (e.g., "MyApp.Jobs.EmailJob").
    /// </summary>
    public string JobTypeName { get; set; }

    /// <summary>
    /// Cron expression for recurring jobs.
    /// </summary>
    public string CronExpression { get; set; }

    /// <summary>
    /// Next scheduled execution time.
    /// </summary>
    public DateTime? NextExecuteAt { get; set; }

    /// <summary>
    /// JSON serialized job data/parameters.
    /// </summary>
    public string JobData { get; set; }

    /// <summary>
    /// Whether the job is active.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Source scheduler type (e.g., "Quartz", "Hangfire").
    /// </summary>
    public string Source { get; set; }

    /// <summary>
    /// Worker/instance identifier.
    /// </summary>
    public string WorkerId { get; set; }

    /// <summary>
    /// Optional tags for categorization.
    /// </summary>
    public string Tags { get; set; }

    /// <summary>
    /// Timestamp when this message was created (UTC).
    /// </summary>
    public DateTime MessageTimestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Message for reporting external job occurrence events to Milvaion.
/// Published to external_job_occurrence queue.
/// </summary>
public class ExternalJobOccurrenceMessage
{
    /// <summary>
    /// External job identifier (e.g., "DEFAULT.MyQuartzJob").
    /// </summary>
    public string ExternalJobId { get; set; }

    /// <summary>
    /// External occurrence identifier (e.g., Quartz FireInstanceId).
    /// </summary>
    public string ExternalOccurrenceId { get; set; }

    /// <summary>
    /// Unique correlation ID for this occurrence.
    /// </summary>
    public Guid CorrelationId { get; set; }

    /// <summary>
    /// Job type name.
    /// </summary>
    public string JobTypeName { get; set; }

    /// <summary>
    /// Worker/instance identifier.
    /// </summary>
    public string WorkerId { get; set; }

    /// <summary>
    /// Type of occurrence event.
    /// </summary>
    public ExternalOccurrenceEventType EventType { get; set; }

    /// <summary>
    /// Occurrence status.
    /// </summary>
    public JobOccurrenceStatus Status { get; set; }

    /// <summary>
    /// Scheduled fire time.
    /// </summary>
    public DateTime? ScheduledFireTime { get; set; }

    /// <summary>
    /// Actual fire time.
    /// </summary>
    public DateTime? ActualFireTime { get; set; }

    /// <summary>
    /// Start time of execution.
    /// </summary>
    public DateTime? StartTime { get; set; }

    /// <summary>
    /// End time of execution.
    /// </summary>
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// Execution duration in milliseconds.
    /// </summary>
    public long? DurationMs { get; set; }

    /// <summary>
    /// Result message (for completed jobs).
    /// </summary>
    public string Result { get; set; }

    /// <summary>
    /// Exception details (for failed jobs).
    /// </summary>
    public string Exception { get; set; }

    /// <summary>
    /// Job source/scheduler name (e.g., "Quartz", "Hangfire").
    /// </summary>
    public string Source { get; set; }

    /// <summary>
    /// Timestamp when this message was created (UTC).
    /// </summary>
    public DateTime MessageTimestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Types of external job occurrence events.
/// </summary>
public enum ExternalOccurrenceEventType
{
    /// <summary>
    /// Job is starting execution.
    /// </summary>
    Starting = 0,

    /// <summary>
    /// Job completed (success or failure).
    /// </summary>
    Completed = 1,

    /// <summary>
    /// Job was vetoed/skipped.
    /// </summary>
    Vetoed = 2,

    /// <summary>
    /// Job was cancelled.
    /// </summary>
    Cancelled = 3
}
