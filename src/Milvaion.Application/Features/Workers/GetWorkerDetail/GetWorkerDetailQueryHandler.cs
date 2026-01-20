using Mapster;
using Milvaion.Application.Dtos.WorkerDtos;
using Milvaion.Application.Interfaces.Redis;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.Workers.GetWorkerDetail;

/// <summary>
/// Handles the worker detail operation.
/// Reads from Redis for real-time state.
/// </summary>
/// <param name="redisWorkerService"></param>
public class GetWorkerDetailQueryHandler(IRedisWorkerService redisWorkerService) : IInterceptable, IQueryHandler<GetWorkerDetailQuery, WorkerDto>
{
    private readonly IRedisWorkerService _redisWorkerService = redisWorkerService;

    /// <inheritdoc/>
    public async Task<Response<WorkerDto>> Handle(GetWorkerDetailQuery request, CancellationToken cancellationToken)
    {
        var cachedWorker = await _redisWorkerService.GetWorkerAsync(request.WorkerId, cancellationToken);

        if (cachedWorker == null)
            return Response<WorkerDto>.Error(default, $"Worker {request.WorkerId} not found");

        var workerDto = cachedWorker.Adapt<WorkerDto>();

        return Response<WorkerDto>.Success(workerDto);
    }
}
