using Milvasoft.Attributes.Annotations;

namespace Milvaion.Application.Dtos.DashboardDtos;

/// <summary>
/// Data transfer object for dashboard statistics.
/// </summary>
[Translate]
public class DashboardDto
{
    /// <summary>
    /// Total number of job executions.
    /// </summary>
    public long TotalExecutions { get; set; }

    /// <summary>
    /// Number of queued jobs.
    /// </summary>
    public long QueuedJobs { get; set; }

    /// <summary>
    /// Number of completed jobs.
    /// </summary>
    public long CompletedJobs { get; set; }

    /// <summary>
    /// Number of failed jobs.
    /// </summary>
    public long FailedOccurrences { get; set; }

    /// <summary>
    /// Number of cancelled jobs.
    /// </summary>
    public long CancelledJobs { get; set; }

    /// <summary>
    /// Number of timed out jobs.
    /// </summary>
    public long TimedOutJobs { get; set; }

    /// <summary>
    /// Number of running jobs.
    /// </summary>
    public long RunningJobs { get; set; }

    /// <summary>
    /// Average execution duration in milliseconds.
    /// </summary>
    public double? AverageDuration { get; set; }

    /// <summary>
    /// Success rate percentage (0-100).
    /// </summary>
    public double SuccessRate { get; set; }

    /// <summary>
    /// Total number of active workers.
    /// </summary>
    public int TotalWorkers { get; set; }

    /// <summary>
    /// Total number of active worker instances.
    /// </summary>
    public int TotalWorkerInstances { get; set; }

    /// <summary>
    /// Total current jobs being processed by all workers.
    /// </summary>
    public int WorkerCurrentJobs { get; set; }

    /// <summary>
    /// Total maximum parallel jobs capacity across all workers.
    /// </summary>
    public int WorkerMaxCapacity { get; set; }

    /// <summary>
    /// Worker utilization percentage (0-100).
    /// </summary>
    public double WorkerUtilization { get; set; }

    /// <summary>
    /// Executions per minute (throughput metric).
    /// </summary>
    public double ExecutionsPerMinute { get; set; }

    /// <summary>
    /// Executions per second (throughput metric).
    /// </summary>
    public double ExecutionsPerSecond { get; set; }

    /// <summary>
    /// Peak executions per minute in the last hour.
    /// </summary>
    public double? PeakExecutionsPerMinute { get; set; }
}
