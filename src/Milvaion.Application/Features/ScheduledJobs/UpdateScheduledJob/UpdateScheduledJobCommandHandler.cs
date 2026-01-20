using Milvaion.Application.Interfaces.Redis;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Ef.Transaction;
using Milvasoft.Interception.Interceptors.Logging;
using System.Text.Json;

namespace Milvaion.Application.Features.ScheduledJobs.UpdateScheduledJob;

/// <summary>
/// Handles the update of the scheduledjob.
/// </summary>
/// <param name="ScheduledJobRepository"></param>
/// <param name="RedisSchedulerService"></param>
/// <param name="EventPublisher"></param>
[Log]
[Transaction]
[UserActivityTrack(UserActivity.UpdateScheduledJob)]
public record UpdateScheduledJobCommandHandler(IMilvaionRepositoryBase<ScheduledJob> ScheduledJobRepository,
                                               IRedisSchedulerService RedisSchedulerService,
                                               IJobOccurrenceEventPublisher EventPublisher) : IInterceptable, ICommandHandler<UpdateScheduledJobCommand, Guid>
{
    private readonly IMilvaionRepositoryBase<ScheduledJob> _scheduledjobRepository = ScheduledJobRepository;
    private readonly IRedisSchedulerService _redisSchedulerService = RedisSchedulerService;
    private readonly IJobOccurrenceEventPublisher _eventPublisher = EventPublisher;

    /// <inheritdoc/>
    public async Task<Response<Guid>> Handle(UpdateScheduledJobCommand request, CancellationToken cancellationToken)
    {
        DateTime? newExecuteAt = null;

        bool jobDefinitionChanged = false;

        var existingJob = await _scheduledjobRepository.GetByIdAsync(request.Id, cancellationToken: cancellationToken);

        if (request.CronExpression.IsUpdated && !string.IsNullOrWhiteSpace(request.CronExpression.Value))
        {
            var cronExpression = Cronos.CronExpression.Parse(request.CronExpression.Value, Cronos.CronFormat.IncludeSeconds);

            newExecuteAt = cronExpression.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Utc)!.Value;

            if (existingJob.CronExpression != request.CronExpression.Value)
                jobDefinitionChanged = true;
        }

        if (request.JobData.IsUpdated)
        {
            ScheduledJob.FixJobData(request.JobData.Value);

            if (existingJob.JobData != request.JobData.Value)
                jobDefinitionChanged = true;
        }

        // Update in database
        var setPropertyBuilder = _scheduledjobRepository.GetUpdatablePropertiesBuilder(request);

        // Override ExecuteAt with calculated value from cron
        if (newExecuteAt.HasValue)
            setPropertyBuilder = setPropertyBuilder.SetPropertyValue(sj => sj.ExecuteAt, newExecuteAt.Value);

        if (request.IsActive.IsUpdated && existingJob.IsActive != request.IsActive.Value)
        {
            existingJob.AutoDisableSettings = new()
            {
                Enabled = existingJob.AutoDisableSettings.Enabled,
                Threshold = existingJob.AutoDisableSettings.Threshold,
                LastFailureTime = existingJob.AutoDisableSettings.LastFailureTime
            };
        }

        if (request.AutoDisableSettings.IsUpdated)
        {
            existingJob.AutoDisableSettings = new()
            {
                Enabled = request.AutoDisableSettings.Value.Enabled,
                Threshold = request.AutoDisableSettings.Value.Threshold
            };

            setPropertyBuilder = setPropertyBuilder.SetPropertyValue(sj => sj.AutoDisableSettings, existingJob.AutoDisableSettings);
        }

        // Check if job definition changed

        if (jobDefinitionChanged)
        {
            existingJob.JobVersions.Add(JsonSerializer.Serialize(existingJob));

            setPropertyBuilder.SetPropertyValue(sj => sj.JobVersions, existingJob.JobVersions);

            setPropertyBuilder.SetProperty(sj => sj.Version, sj => existingJob.Version + 1);
        }

        await _scheduledjobRepository.ExecuteUpdateAsync(request.Id, setPropertyBuilder, cancellationToken: cancellationToken);

        // Update Redis cache (partial update for changed fields)
        var cacheUpdates = new Dictionary<string, object>();

        if (request.DisplayName.IsUpdated)
            cacheUpdates["DisplayName"] = request.DisplayName.Value ?? string.Empty;

        if (request.Description.IsUpdated)
            cacheUpdates["Description"] = request.Description.Value ?? string.Empty;

        if (request.Tags.IsUpdated)
            cacheUpdates["Tags"] = request.Tags.Value ?? string.Empty;

        if (request.JobType.IsUpdated)
            cacheUpdates["JobType"] = request.JobType.Value;

        if (request.JobData.IsUpdated)
            cacheUpdates["JobData"] = request.JobData.Value ?? string.Empty;

        if (newExecuteAt.HasValue)
        {
            cacheUpdates["ExecuteAt"] = newExecuteAt.Value.ToString("O");
            await _redisSchedulerService.UpdateScheduleAsync(request.Id, newExecuteAt.Value, cancellationToken);
        }

        if (request.CronExpression.IsUpdated)
            cacheUpdates["CronExpression"] = request.CronExpression.Value ?? string.Empty;

        if (request.ConcurrentExecutionPolicy.IsUpdated)
            cacheUpdates["ConcurrentExecutionPolicy"] = ((int)request.ConcurrentExecutionPolicy.Value).ToString();

        if (request.ZombieTimeoutMinutes.IsUpdated)
            cacheUpdates["ZombieTimeoutMinutes"] = request.ZombieTimeoutMinutes.Value?.ToString() ?? string.Empty;

        if (request.ExecutionTimeoutSeconds.IsUpdated)
            cacheUpdates["ExecutionTimeoutSeconds"] = request.ExecutionTimeoutSeconds.Value?.ToString() ?? string.Empty;

        if (jobDefinitionChanged)
        {
            cacheUpdates["Version"] = (existingJob.Version + 1).ToString();
        }

        if (request.IsActive.IsUpdated)
        {
            cacheUpdates["IsActive"] = request.IsActive.Value.ToString();

            if (!request.IsActive.Value)
            {
                await _redisSchedulerService.RemoveFromScheduledSetAsync(request.Id, cancellationToken);
            }
            else
            {
                var job = await _scheduledjobRepository.GetByIdAsync(request.Id, cancellationToken: cancellationToken);

                if (job != null)
                {
                    await _redisSchedulerService.AddToScheduledSetAsync(job.Id, job.ExecuteAt, cancellationToken);
                }
            }
        }

        if (cacheUpdates.Count != 0)
            await _redisSchedulerService.UpdateCachedJobFieldsAsync(request.Id, cacheUpdates, cancellationToken);

        // Publish SignalR event for real-time UI update
        var updateData = new Dictionary<string, object> { ["id"] = request.Id };

        if (request.DisplayName.IsUpdated)
            updateData["displayName"] = request.DisplayName.Value;

        if (request.JobType.IsUpdated)
            updateData["jobType"] = request.JobType.Value;

        if (request.JobData.IsUpdated)
            updateData["jobData"] = request.JobData.Value;

        if (request.CronExpression.IsUpdated)
            updateData["cronExpression"] = request.CronExpression.Value;

        if (request.IsActive.IsUpdated)
            updateData["isActive"] = request.IsActive.Value;

        return Response<Guid>.Success(request.Id);
    }
}
