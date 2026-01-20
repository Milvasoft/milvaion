using Milvaion.Application.Interfaces.Redis;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.Workers.DeleteWorker;

/// <summary>
/// Handles the deletion of the worker from Redis.
/// </summary>
[Log]
[UserActivityTrack(UserActivity.DeleteScheduledJob)]
public record DeleteWorkerCommandHandler(IRedisWorkerService RedisWorkerService) : IInterceptable, ICommandHandler<DeleteWorkerCommand, string>
{
    private readonly IRedisWorkerService _redisWorkerService = RedisWorkerService;

    /// <inheritdoc/>
    public async Task<Response<string>> Handle(DeleteWorkerCommand request, CancellationToken cancellationToken)
    {
        var worker = await _redisWorkerService.GetWorkerAsync(request.WorkerId, cancellationToken);

        if (worker == null)
            return Response<string>.Error(default, MessageKey.WorkerNotFound);

        var success = await _redisWorkerService.RemoveWorkerAsync(request.WorkerId, cancellationToken);

        if (!success)
            return Response<string>.Error(default, "Failed to delete worker from Redis");

        return Response<string>.Success(request.WorkerId);
    }
}
