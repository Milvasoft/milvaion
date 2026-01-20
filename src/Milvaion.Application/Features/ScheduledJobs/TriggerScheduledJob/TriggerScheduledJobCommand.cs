using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Features.ScheduledJobs.TriggerScheduledJob;

/// <summary>
/// Command to manually trigger a scheduled job (create occurrence and dispatch immediately).
/// </summary>
public record TriggerScheduledJobCommand : ICommand<Guid>
{
    /// <summary>
    /// Job ID to trigger.
    /// </summary>
    public Guid JobId { get; set; }

    /// <summary>
    /// Trigger reason (optional).
    /// </summary>
    public string Reason { get; set; }

    /// <summary>
    /// JSON serialized payload data required for job execution.
    /// </summary>
    public string JobData { get; set; }

    /// <summary>
    /// Force trigger - bypasses ConcurrentPolicy and MaxConcurrent checks (admin only).
    /// Default: false (respect policy).
    /// </summary>
    public bool Force { get; set; } = false;
}
