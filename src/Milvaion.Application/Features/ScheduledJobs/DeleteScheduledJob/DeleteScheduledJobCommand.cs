using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Features.ScheduledJobs.DeleteScheduledJob;

/// <summary>
/// Data transfer object for scheduledjob deletion.
/// </summary>
public record DeleteScheduledJobCommand : ICommand<Guid>
{
    /// <summary>
    /// Id of the scheduledjob to be deleted.
    /// </summary>
    public Guid JobId { get; set; }
}
