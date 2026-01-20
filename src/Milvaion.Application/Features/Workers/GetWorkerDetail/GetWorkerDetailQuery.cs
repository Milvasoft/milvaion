using Milvaion.Application.Dtos.WorkerDtos;
using Milvasoft.Components.CQRS.Query;

namespace Milvaion.Application.Features.Workers.GetWorkerDetail;

/// <summary>
/// Data transfer object for scheduledjob details.
/// </summary>
public record GetWorkerDetailQuery : IQuery<WorkerDto>
{
    /// <summary>
    /// Worker ID to access details.
    /// </summary>
    public string WorkerId { get; set; }
}
