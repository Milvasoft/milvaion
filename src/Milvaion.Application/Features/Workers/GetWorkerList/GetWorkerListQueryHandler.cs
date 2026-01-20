using Mapster;
using Milvaion.Application.Dtos.WorkerDtos;
using Milvaion.Application.Interfaces.Redis;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.Workers.GetWorkerList;

/// <summary>
/// Handles the worker list operation.
/// Reads from Redis for real-time worker state.
/// </summary>
/// <param name="redisWorkerService"></param>
public class GetWorkerListQueryHandler(IRedisWorkerService redisWorkerService) : IInterceptable, IQueryHandler<GetWorkerListQuery, List<WorkerDto>>
{
    private readonly IRedisWorkerService _redisWorkerService = redisWorkerService;

    /// <inheritdoc/>
    public async Task<Response<List<WorkerDto>>> Handle(GetWorkerListQuery request, CancellationToken cancellationToken)
    {
        // Get all workers from Redis (real-time state)
        var workers = await _redisWorkerService.GetAllWorkersAsync(cancellationToken);

        // Convert to DTOs
        var workerDtos = workers.Adapt<List<WorkerDto>>();

        // Return list response
        return new ListResponse<WorkerDto>
        {
            Data = workerDtos,
            TotalDataCount = workerDtos.Count
        };
    }
}
