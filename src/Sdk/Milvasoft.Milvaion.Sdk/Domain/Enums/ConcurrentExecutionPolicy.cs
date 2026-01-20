namespace Milvasoft.Milvaion.Sdk.Domain.Enums;

/// <summary>
/// Defines behavior when a job is triggered while a previous occurrence is still running.
/// </summary>
public enum ConcurrentExecutionPolicy
{
    /// <summary>
    /// Skip the new execution if the job is already running.
    /// New occurrence will NOT be created. A log message "Already running" will be recorded.
    /// This is the safest option for jobs that should not run concurrently.
    /// </summary>
    Skip = 0,

    /// <summary>
    /// Queue the new execution to run after the current one completes.
    /// New occurrence will be created and wait in queue.
    /// Use for jobs where every execution is important and order matters.
    /// </summary>
    Queue = 1
}
