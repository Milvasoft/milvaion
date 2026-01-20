using Milvaion.Application.Dtos.ScheduledJobDtos;
using Milvasoft.Attributes.Annotations;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Types.Structs;

namespace Milvaion.Application.Features.ScheduledJobs.UpdateScheduledJob;

/// <summary>
/// Data transfer object for scheduledjob update.
/// </summary>
public class UpdateScheduledJobCommand : MilvaionBaseDto<Guid>, ICommand<Guid>
{
    /// <summary>
    /// Display name of the scheduled job.
    /// </summary>
    public UpdateProperty<string> DisplayName { get; set; }

    /// <summary>
    /// Description of the scheduled job.
    /// </summary>
    public UpdateProperty<string> Description { get; set; }

    /// <summary>
    /// Comma seperated tags of the scheduled job.
    /// </summary>
    public UpdateProperty<string> Tags { get; set; }

    /// <summary>
    /// Type identifier for the job handler (e.g., "SendEmailJob", "ProcessOrderJob").
    /// </summary>
    public UpdateProperty<string> JobType { get; set; }

    /// <summary>
    /// JSON serialized payload data required for job execution.
    /// </summary>
    public UpdateProperty<string> JobData { get; set; }

    /// <summary>
    /// Cron expression for recurring job scheduling (e.g., "0 9 * * MON" for every Monday at 9 AM).
    /// Supports standard cron format (minute, hour, day of month, month, day of week).
    /// If null, the job is a one-time job that executes at ExecuteAt and is not rescheduled.
    /// </summary>
    public UpdateProperty<string> CronExpression { get; set; }

    /// <summary>
    /// Indicates whether the job is active and should be processed by the dispatcher.
    /// Inactive jobs are skipped during scheduling. Users can toggle this via dashboard.
    /// </summary>
    public UpdateProperty<bool> IsActive { get; set; }

    /// <summary>
    /// Defines behavior when a job is triggered while a previous occurrence is still running.
    /// </summary>
    public UpdateProperty<ConcurrentExecutionPolicy> ConcurrentExecutionPolicy { get; set; }

    /// <summary>
    /// Job-specific zombie timeout in minutes.
    /// If set, occurrences stuck in Queued status longer than this will be marked as Failed.
    /// If null, global ZombieDetector timeout (10 minutes) is used.
    /// Useful for long-running jobs that need higher timeout thresholds.
    /// </summary>
    public UpdateProperty<int?> ZombieTimeoutMinutes { get; set; }

    /// <summary>
    /// Job-specific execution timeout in seconds.
    /// If set, worker will cancel the job after this duration and mark it as TimedOut.
    /// If null, worker's JobConsumerConfig.ExecutionTimeoutSeconds is used as fallback.
    /// Default: null (use worker config, typically 3600 seconds = 1 hour).
    /// </summary>
    public UpdateProperty<int?> ExecutionTimeoutSeconds { get; set; }

    /// <summary>
    /// Auto-disable settings for the scheduled job.
    /// </summary>
    [UpdatableIgnore]
    public UpdateProperty<UpsertJobAutoDisableSettings> AutoDisableSettings { get; set; } = new();
}
