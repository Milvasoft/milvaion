using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.EntityBases.Concrete.Auditing;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Domain.JsonModels;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;

namespace Milvasoft.Milvaion.Sdk.Domain;

/// <summary>
/// Entity of the ScheduledJobs table. Represents a background job scheduled for future execution.
/// </summary>
[Table(SchedulerTableNames.ScheduledJobs)]
[DontIndexCreationDate]
public class ScheduledJob : AuditableEntity<Guid>
{
    /// <summary>
    /// Display name of the scheduled job.
    /// </summary>
    public string DisplayName { get; set; }

    /// <summary>
    /// Description of the scheduled job.
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// Comma separated Tags of the scheduled job.
    /// </summary>
    public string Tags { get; set; }

    /// <summary>
    /// JSON serialized payload data required for job execution.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public string JobData { get; set; }

    /// <summary>
    /// Scheduled execution time (UTC). Dispatcher will trigger the job at or after this time.
    /// </summary>
    /// <remarks>
    /// This is the <em>configured</em> start time, written when the job is created or updated.
    /// It is not the next run of a recurring job: the dispatcher advances that in memory and in
    /// the Redis sorted set, and never writes it back here. For anything that needs the live
    /// schedule, read Redis - this column will be in the past once the job has run.
    /// </remarks>
    [Required]
    public DateTime ExecuteAt { get; set; }

    /// <summary>
    /// When a one-time job was dispatched (UTC). Null for recurring jobs and for one-time jobs that have not run yet.
    /// </summary>
    /// <remarks>
    /// Marks a one-time job as finished so it is never scheduled again. Before this existed, the
    /// only record that such a job had run was its absence from the Redis sorted set - and Redis
    /// is a cache, so a flush or a restart lost that fact and startup recovery re-scheduled the
    /// job, running it a second time.
    ///
    /// Kept separate from <see cref="IsActive"/> on purpose: "finished" and "switched off by a
    /// user" are different states, and collapsing them would make a completed job look disabled
    /// and let re-enabling it fire again.
    /// </remarks>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Cron expression for recurring job scheduling (e.g., "0 9 * * MON" for every Monday at 9 AM).
    /// Supports standard cron format (minute, hour, day of month, month, day of week).
    /// If null, the job is a one-time job that executes at ExecuteAt and is not rescheduled.
    /// </summary>
    [MaxLength(100)]
    public string CronExpression { get; set; }

    /// <summary>
    /// Indicates whether the job is active and should be processed by the dispatcher.
    /// Inactive jobs are skipped during scheduling. Users can toggle this via dashboard.
    /// </summary>
    [Required]
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Defines behavior when a job is triggered while a previous occurrence is still running.
    /// Default: Skip - do not create new occurrence if job is already running.
    /// </summary>
    [Required]
    public ConcurrentExecutionPolicy ConcurrentExecutionPolicy { get; set; } = ConcurrentExecutionPolicy.Skip;

    /// <summary>
    /// Target worker ID that should execute this job.
    /// If specified, dispatcher will send job to this specific worker's routing pattern.
    /// If null, job can be executed by any worker matching the routing pattern.
    /// </summary>
    [MaxLength(100)]
    public string WorkerId { get; set; }

    /// <summary>
    /// Related job identifier for the job handler (e.g., "SendEmailJob", "ProcessOrderJob").
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string JobNameInWorker { get; set; }

    /// <summary>
    /// Routing pattern for this job (copied from worker at creation time).
    /// </summary>
    public string RoutingPattern { get; set; }

    /// <summary>
    /// Job-specific zombie timeout in minutes.
    /// If set, occurrences stuck in Queued status longer than this will be marked as Failed.
    /// If null, global ZombieDetector timeout (10 minutes) is used.
    /// Useful for long-running jobs that need higher timeout thresholds.
    /// </summary>
    public int? ZombieTimeoutMinutes { get; set; }

    /// <summary>
    /// Job-specific execution timeout in seconds.
    /// If set, worker will cancel the job after this duration and mark it as TimedOut.
    /// If null, worker's JobConsumerConfig.ExecutionTimeoutSeconds is used as fallback.
    /// Default: null (use worker config, typically 3600 seconds = 1 hour).
    /// </summary>
    public int? ExecutionTimeoutSeconds { get; set; }

    /// <summary>
    /// Indicates whether this job is from an external scheduler (Quartz, Hangfire, etc.).
    /// External jobs are not dispatched by Milvaion - they only report their occurrences for monitoring.
    /// </summary>
    public bool IsExternal { get; set; }

    /// <summary>
    /// External job identifier for mapping (e.g., "DEFAULT.MyQuartzJob").
    /// Used to correlate occurrences from external schedulers.
    /// </summary>
    [MaxLength(500)]
    public string ExternalJobId { get; set; }

    /// <summary>
    /// Job data version for schema evolution and compatibility.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Job versions history.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public List<string> JobVersions { get; set; } = [];

    /// <summary>
    /// Job auto-disable settings.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public JobAutoDisableSettings AutoDisableSettings { get; set; } = new();

    /// <summary>
    /// Occurrences of this scheduled job.
    /// </summary>
    [CascadeOnDelete]
    public virtual List<JobOccurrence> Occurrences { get; set; }

    /// <summary>
    /// Failed occurrences of this scheduled job.
    /// </summary>
    [CascadeOnDelete]
    public virtual List<FailedOccurrence> FailedOccurrences { get; set; }

    public string FixJobData() => JobData = FixJobData(JobData);

    public static string FixJobData(string jobData)
    {
        // 2.5. Validate and sanitize JobData (prevent JSON errors)
        if (!string.IsNullOrWhiteSpace(jobData))
        {
            try
            {
                // Test if JobData is valid JSON
                System.Text.Json.JsonDocument.Parse(jobData);
            }
            catch (System.Text.Json.JsonException)
            {
                // Sanitize to valid empty JSON
                jobData = "{}";
            }
        }
        else
        {
            // Ensure null/empty is valid JSON
            jobData = jobData == null ? null : "{}";
        }

        return jobData;
    }

    public static class Projections
    {
        public static Expression<Func<ScheduledJob, ScheduledJob>> TagList { get; } = s => new ScheduledJob
        {
            Id = s.Id,
            Tags = s.Tags
        };

        public static Expression<Func<ScheduledJob, ScheduledJob>> OnlyId { get; } = s => new ScheduledJob
        {
            Id = s.Id,
        };

        /// <summary>
        /// Projection for cache/dispatcher use.
        /// </summary>
        public static Expression<Func<ScheduledJob, ScheduledJob>> CacheJob { get; } = s => new ScheduledJob
        {
            Id = s.Id,
            DisplayName = s.DisplayName,
            Description = s.Description,
            Tags = s.Tags,
            JobNameInWorker = s.JobNameInWorker,
            JobData = s.JobData,
            CronExpression = s.CronExpression,
            ConcurrentExecutionPolicy = s.ConcurrentExecutionPolicy,
            WorkerId = s.WorkerId,
            RoutingPattern = s.RoutingPattern,
            ExecuteAt = s.ExecuteAt,
            CompletedAt = s.CompletedAt,
            ZombieTimeoutMinutes = s.ZombieTimeoutMinutes,
            ExecutionTimeoutSeconds = s.ExecutionTimeoutSeconds,
            Version = s.Version,
            IsActive = s.IsActive,
            CreationDate = s.CreationDate,
            CreatorUserName = s.CreatorUserName,
            AutoDisableSettings = s.AutoDisableSettings
        };

        /// <summary>
        /// Projection for retry/occurrence dispatch.
        /// </summary>
        public static Expression<Func<ScheduledJob, ScheduledJob>> RetryFailedOccurrence { get; } = s => new ScheduledJob
        {
            Id = s.Id,
            ConcurrentExecutionPolicy = s.ConcurrentExecutionPolicy,
            WorkerId = s.WorkerId,
            Description = s.Description,
            RoutingPattern = s.RoutingPattern,
            ZombieTimeoutMinutes = s.ZombieTimeoutMinutes,
            ExecutionTimeoutSeconds = s.ExecutionTimeoutSeconds,
            Version = s.Version,
            DisplayName = s.DisplayName,
            JobNameInWorker = s.JobNameInWorker,
            JobData = s.JobData,
            CronExpression = s.CronExpression,
            IsActive = s.IsActive,
            CreationDate = s.CreationDate,
            CreatorUserName = s.CreatorUserName,
        };

        /// <summary>
        /// Projection for circuit breaker updates.
        /// </summary>
        public static Expression<Func<ScheduledJob, ScheduledJob>> CircuitBreaker { get; } = s => new ScheduledJob
        {
            Id = s.Id,
            DisplayName = s.DisplayName,
            JobNameInWorker = s.JobNameInWorker,
            IsActive = s.IsActive,
            CronExpression = s.CronExpression,
            AutoDisableSettings = s.AutoDisableSettings
        };

        /// <summary>
        /// Projection for circuit breaker updates.
        /// </summary>
        public static Expression<Func<ScheduledJob, ScheduledJob>> OccurrenceJobData { get; } = s => new ScheduledJob
        {
            Id = s.Id,
            DisplayName = s.DisplayName,
        };
    }
}
