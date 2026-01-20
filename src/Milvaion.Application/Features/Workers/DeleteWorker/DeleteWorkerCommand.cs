using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Features.Workers.DeleteWorker;

/// <summary>
/// Data transfer object for worker deletion.
/// </summary>
public record DeleteWorkerCommand : ICommand<string>
{
    /// <summary>
    /// Worker ID to delete.
    /// </summary>
    public string WorkerId { get; set; }
}
