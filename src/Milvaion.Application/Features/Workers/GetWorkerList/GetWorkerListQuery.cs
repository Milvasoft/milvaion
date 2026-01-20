using Milvaion.Application.Dtos.WorkerDtos;
using Milvasoft.Components.CQRS.Query;

namespace Milvaion.Application.Features.Workers.GetWorkerList;

/// <summary>
/// Data transfer object for scheduledjob list.
/// </summary>
public record GetWorkerListQuery : IQuery<List<WorkerDto>>
{
}