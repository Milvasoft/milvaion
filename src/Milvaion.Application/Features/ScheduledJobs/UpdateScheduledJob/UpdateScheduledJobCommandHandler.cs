using Milvaion.Application.Interfaces.Redis;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Ef.Transaction;
using Milvasoft.Interception.Interceptors.Logging;
using Milvasoft.Types.Structs;

namespace Milvaion.Application.Features.ScheduledJobs.UpdateScheduledJob;

/// <summary>
/// Handles the update of the scheduledjob.
/// </summary>
/// <param name="ScheduledJobRepository"></param>
/// <param name="ExclusionCalendarRepository"></param>
/// <param name="RedisSchedulerService"></param>
/// <param name="RedisWorkerService"></param>
[Log]
[Transaction]
[UserActivityTrack(UserActivity.UpdateScheduledJob)]
public record UpdateScheduledJobCommandHandler(IMilvaionRepositoryBase<ScheduledJob> ScheduledJobRepository,
                                               IMilvaionRepositoryBase<ExclusionCalendar> ExclusionCalendarRepository,
                                               IRedisSchedulerService RedisSchedulerService,
                                               IRedisWorkerService RedisWorkerService) : IInterceptable, ICommandHandler<UpdateScheduledJobCommand, Guid>
{
    private readonly IMilvaionRepositoryBase<ScheduledJob> _scheduledjobRepository = ScheduledJobRepository;
    private readonly IMilvaionRepositoryBase<ExclusionCalendar> _exclusionCalendarRepository = ExclusionCalendarRepository;
    private readonly IRedisSchedulerService _redisSchedulerService = RedisSchedulerService;
    private readonly IRedisWorkerService _redisWorkerService = RedisWorkerService;

    /// <inheritdoc/>
    public async Task<Response<Guid>> Handle(UpdateScheduledJobCommand request, CancellationToken cancellationToken)
    {
        DateTime? newExecuteAt = null;

        bool jobDefinitionChanged = false;

        var existingJob = await _scheduledjobRepository.GetByIdAsync(request.Id, cancellationToken: cancellationToken);

        if (existingJob == null)
            return Response<Guid>.Error(default, MessageKey.JobNotFound);

        // External jobs have restricted update capabilities
        if (!IsValidForExternalUpdate(existingJob, request))
            return Response<Guid>.Error(existingJob.Id, "External job cannot modified!");

        // A cleared zone means UTC, so the column never holds an empty value.
        if (request.TimeZoneId.IsUpdated && string.IsNullOrWhiteSpace(request.TimeZoneId.Value))
            request.TimeZoneId = new UpdateProperty<string> { Value = ScheduledJob.DefaultTimeZoneId, IsUpdated = true };

        if (request.ExclusionCalendarIds.IsUpdated)
        {
            var calendarIds = ExclusionCalendarReferences.Normalize(request.ExclusionCalendarIds.Value);

            var missingCalendars = await ExclusionCalendarReferences.FindMissingAsync(_exclusionCalendarRepository, calendarIds, cancellationToken);

            if (missingCalendars is not null)
                return Response<Guid>.Error(default, missingCalendars);

            request.ExclusionCalendarIds = new UpdateProperty<List<Guid>> { Value = calendarIds, IsUpdated = true };
        }

        var effectiveZone = ZonedCron.ResolveTimeZone(request.TimeZoneId.IsUpdated ? request.TimeZoneId.Value : existingJob.TimeZoneId);
        var timeZoneChanged = request.TimeZoneId.IsUpdated && request.TimeZoneId.Value != (existingJob.TimeZoneId ?? ScheduledJob.DefaultTimeZoneId);

        if (request.CronExpression.IsUpdated && !string.IsNullOrWhiteSpace(request.CronExpression.Value))
        {
            var cronExpression = ZonedCron.Parse(request.CronExpression.Value);

            newExecuteAt = ZonedCron.GetNextOccurrence(cronExpression, DateTime.UtcNow, effectiveZone);

            if (existingJob.CronExpression != request.CronExpression.Value)
                jobDefinitionChanged = true;
        }

        // Same cron, new zone: the next run moves to the same wall time in the new zone.
        if (!newExecuteAt.HasValue && timeZoneChanged && !request.CronExpression.IsUpdated
            && ZonedCron.TryParse(existingJob.CronExpression, out var existingCron))
            newExecuteAt = ZonedCron.GetNextOccurrence(existingCron, DateTime.UtcNow, effectiveZone);

        if (request.JobData.IsUpdated)
        {
            request.JobData = new UpdateProperty<string> { Value = ScheduledJob.FixJobData(request.JobData.Value), IsUpdated = true };

            if (existingJob.JobData != request.JobData.Value)
                jobDefinitionChanged = true;
        }

        // The command calls it JobType but the column is JobNameInWorker, so the builder below cannot map it by name.
        var newJobName = request.JobType.IsUpdated && !string.IsNullOrWhiteSpace(request.JobType.Value) ? request.JobType.Value : null;
        string newRoutingPattern = null;

        if (newJobName is not null && newJobName != existingJob.JobNameInWorker)
        {
            jobDefinitionChanged = true;

            // The routing pattern is per job name, so a stale one would keep sending the job to the old queue.
            if (!string.IsNullOrWhiteSpace(existingJob.WorkerId))
            {
                var worker = await _redisWorkerService.GetWorkerAsync(existingJob.WorkerId, cancellationToken);

                if (worker?.RoutingPatterns?.TryGetValue(newJobName, out var routingPattern) == true)
                    newRoutingPattern = routingPattern;
            }
        }

        var reactivating = request.IsActive.IsUpdated && request.IsActive.Value && !existingJob.IsActive;

        // ExecuteAt is frozen at the last create/update, so for a recurring job that has already run it is in the
        // past. Scheduling on it would fire the job the moment it is switched on.
        var effectiveCron = request.CronExpression.IsUpdated ? request.CronExpression.Value : existingJob.CronExpression;
        var hasCron = !string.IsNullOrWhiteSpace(effectiveCron);

        if (!newExecuteAt.HasValue && request.IsActive.IsUpdated && request.IsActive.Value && hasCron
            && ZonedCron.TryParse(effectiveCron, out var activeCron))
            newExecuteAt = ZonedCron.GetNextOccurrence(activeCron, DateTime.UtcNow, effectiveZone);

        // Update in database
        var setPropertyBuilder = _scheduledjobRepository.GetUpdatablePropertiesBuilder(request);

        // Override ExecuteAt with calculated value from cron
        if (newExecuteAt.HasValue)
            setPropertyBuilder = setPropertyBuilder.SetPropertyValue(sj => sj.ExecuteAt, newExecuteAt.Value);

        if (newJobName is not null)
            setPropertyBuilder = setPropertyBuilder.SetPropertyValue(sj => sj.JobNameInWorker, newJobName);

        if (newRoutingPattern is not null)
            setPropertyBuilder = setPropertyBuilder.SetPropertyValue(sj => sj.RoutingPattern, newRoutingPattern);

        JobAutoDisableSettings newAutoDisableSettings = null;

        // A job that comes back on starts with a clean failure count, otherwise its first failure disables it again.
        if (reactivating)
        {
            newAutoDisableSettings = new()
            {
                Enabled = existingJob.AutoDisableSettings?.Enabled,
                Threshold = existingJob.AutoDisableSettings?.Threshold,
                FailureWindowMinutes = existingJob.AutoDisableSettings?.FailureWindowMinutes,
                LastFailureTime = existingJob.AutoDisableSettings?.LastFailureTime
            };
        }

        if (request.AutoDisableSettings.IsUpdated)
        {
            newAutoDisableSettings = new()
            {
                Enabled = request.AutoDisableSettings.Value.Enabled,
                Threshold = request.AutoDisableSettings.Value.Threshold,
                FailureWindowMinutes = request.AutoDisableSettings.Value.FailureWindowMinutes
            };
        }

        if (newAutoDisableSettings is not null)
            setPropertyBuilder = setPropertyBuilder.SetPropertyValue(sj => sj.AutoDisableSettings, newAutoDisableSettings);

        // Check if job definition changed
        if (jobDefinitionChanged)
        {
            existingJob.JobVersions = JobVersionHistory.Append(existingJob.JobVersions, existingJob);

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

        if (newJobName is not null)
            cacheUpdates["JobType"] = newJobName;

        if (newRoutingPattern is not null)
            cacheUpdates["RoutingPattern"] = newRoutingPattern;

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

        if (request.TimeZoneId.IsUpdated)
            cacheUpdates["TimeZoneId"] = request.TimeZoneId.Value;

        if (request.MissedRunPolicy.IsUpdated)
            cacheUpdates["MissedRunPolicy"] = ((int)request.MissedRunPolicy.Value).ToString();

        if (request.MaxCatchUpRuns.IsUpdated)
            cacheUpdates["MaxCatchUpRuns"] = request.MaxCatchUpRuns.Value.ToString();

        if (request.Priority.IsUpdated)
            cacheUpdates["Priority"] = request.Priority.Value.ToString();

        if (request.ExclusionCalendarIds.IsUpdated)
            cacheUpdates["ExclusionCalendarIds"] = string.Join(',', request.ExclusionCalendarIds.Value ?? []);

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
            else if (newExecuteAt.HasValue || !hasCron)
            {
                // A cron with no future occurrence has nothing left to schedule.
                await _redisSchedulerService.AddToScheduledSetAsync(request.Id, newExecuteAt ?? existingJob.ExecuteAt, cancellationToken);
            }
        }

        if (cacheUpdates.Count != 0)
            await _redisSchedulerService.UpdateCachedJobFieldsAsync(request.Id, cacheUpdates, cancellationToken);

        return Response<Guid>.Success(request.Id);
    }

    private static bool IsValidForExternalUpdate(ScheduledJob job, UpdateScheduledJobCommand request)
    {
        if (job.IsExternal && !request.InternalRequest)
            if (request.JobData.IsUpdated ||
                request.CronExpression.IsUpdated ||
                request.IsActive.IsUpdated ||
                request.ConcurrentExecutionPolicy.IsUpdated ||
                request.JobType.IsUpdated ||
                request.AutoDisableSettings.IsUpdated)
                return false;

        return true;
    }
}
