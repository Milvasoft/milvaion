using Milvaion.Application.Interfaces.Redis;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.Enums;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Helpers;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.ScheduledJobs.DeleteJobOccurrence;

/// <summary>
/// Handles the deletion of a job occurrence.
/// </summary>
[Log]
[UserActivityTrack(UserActivity.DeleteJobOccurrence)]
public record DeleteJobOccurrenceCommandHandler(IMilvaionRepositoryBase<JobOccurrence> OccurrenceRepository, IRedisSchedulerService RedisSchedulerService) : IInterceptable, ICommandHandler<DeleteJobOccurrenceCommand, List<Guid>>
{
    private readonly IMilvaionRepositoryBase<JobOccurrence> _occurrenceRepository = OccurrenceRepository;
    private readonly IRedisSchedulerService _redisSchedulerService = RedisSchedulerService;

    /// <inheritdoc/>
    public async Task<Response<List<Guid>>> Handle(DeleteJobOccurrenceCommand request, CancellationToken cancellationToken)
    {
        //Get occurrence for validation
        var occurrences = await _occurrenceRepository.GetAllAsync(o => request.OccurrenceIdList.Contains(o.Id), cancellationToken: cancellationToken);

        if (occurrences.IsNullOrEmpty())
            return Response<List<Guid>>.Error(default, "Job occurrence not found");

        var canBeDeletedOccurrences = occurrences.Where(o => o.Status != JobOccurrenceStatus.Running && o.Status != JobOccurrenceStatus.Queued).ToList();

        if (canBeDeletedOccurrences.IsNullOrEmpty())
            return Response<List<Guid>>.Error(default, "Job occurrence not found");

        var jobIds = canBeDeletedOccurrences.Select(j => j.JobId).ToList();

        await _redisSchedulerService.RemoveFromScheduledSetBulkAsync(jobIds, cancellationToken);

        await _occurrenceRepository.DeleteAsync(canBeDeletedOccurrences, cancellationToken: cancellationToken);

        // Return deleted job IDs
        var response = Response<List<Guid>>.Success([.. canBeDeletedOccurrences.Select(o => o.Id)], $"Deletion successful for {canBeDeletedOccurrences.Count} executions.");

        var cannotDeletedOccurrences = occurrences.Where(o => o.Status == JobOccurrenceStatus.Running || o.Status == JobOccurrenceStatus.Queued).ToList();

        if (!cannotDeletedOccurrences.IsNullOrEmpty())
            response.AddMessage($"{cannotDeletedOccurrences.Count} occurrences cannot delete because they are running or queued.", MessageType.Warning);

        return response;
    }
}
