using Milvaion.Application.Dtos.ScheduledJobDtos;
using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Features.ScheduledJobs.CreateScheduledJob;

/// <summary>
/// Data transfer object for scheduledjob creation.
/// </summary>
public record CreateScheduledJobCommand : ICommand<Guid>
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
    public string JobData { get; set; }

    /// <summary>
    /// Scheduled execution time (UTC). Dispatcher will trigger the job at or after this time.
    /// For recurring jobs, this is automatically updated to the next execution time based on CronExpression.
    /// </summary>
    public DateTime ExecuteAt { get; set; }

    /// <summary>
    /// Cron expression for recurring job scheduling (e.g., "0 9 * * MON" for every Monday at 9 AM).
    /// Supports standard cron format (minute, hour, day of month, month, day of week).
    /// If null, the job is a one-time job that executes at ExecuteAt and is not rescheduled.
    /// </summary>
    public string CronExpression { get; set; }

    /// <summary>
    /// Indicates whether the job is active and should be processed by the dispatcher.
    /// Inactive jobs are skipped during scheduling. Users can toggle this via dashboard.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Defines behavior when a job is triggered while a previous occurrence is still running.
    /// Default: Skip - do not create new occurrence if job is already running.
    /// </summary>
    public ConcurrentExecutionPolicy ConcurrentExecutionPolicy { get; set; } = ConcurrentExecutionPolicy.Skip;

    /// <summary>
    /// Target worker ID that should execute this job.
    /// If not specified, job can be executed by any compatible worker.
    /// </summary>
    public string WorkerId { get; set; }

    /// <summary>
    /// Specific job type to execute on the selected worker.
    /// If WorkerId is specified, this determines which job implementation to use.
    /// </summary>
    public string SelectedJobName { get; set; }

    /// <summary>
    /// Job-specific zombie timeout in minutes.
    /// If set, occurrences stuck in Queued status longer than this will be marked as Failed.
    /// If null, global ZombieDetector timeout is used.
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
    /// Auto-disable settings for the scheduled job.
    /// </summary>
    public UpsertJobAutoDisableSettings AutoDisableSettings { get; set; } = new();
}
