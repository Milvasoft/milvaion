using Microsoft.AspNetCore.Http;
using Milvaion.Application.Interfaces.RabbitMQ;
using Milvaion.Application.Interfaces.Redis;
using Milvaion.Application.Interfaces.Secrets;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.DataAccess.EfCore.Utils;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.ScheduledJobs.TriggerScheduledJob;

/// <summary>
/// Handles manual job triggering by creating occurrence and dispatching immediately.
/// </summary>
[Log]
[UserActivityTrack(UserActivity.TriggerScheduledJob)]
public record TriggerScheduledJobCommandHandler(IMilvaionRepositoryBase<ScheduledJob> JobRepository,
                                                IMilvaionRepositoryBase<JobOccurrence> OccurrenceRepository,
                                                IMilvaionRepositoryBase<JobOccurrenceLog> OccurrenceLogRepository,
                                                IRabbitMQPublisher RabbitMQPublisher,
                                                IRedisSchedulerService RedisScheduler,
                                                IRedisStatsService StatsService,
                                                IJobOccurrenceEventPublisher EventPublisher,
                                                IMilvaLogger Logger,
                                                IHttpContextAccessor HttpContextAccessor) : IInterceptable, ICommandHandler<TriggerScheduledJobCommand, Guid>
{
    private readonly IMilvaionRepositoryBase<ScheduledJob> _jobRepository = JobRepository;
    private readonly IMilvaionRepositoryBase<JobOccurrence> _occurrenceRepository = OccurrenceRepository;
    private readonly IMilvaionRepositoryBase<JobOccurrenceLog> _occurrenceLogRepository = OccurrenceLogRepository;
    private readonly IRabbitMQPublisher _rabbitMQPublisher = RabbitMQPublisher;
    private readonly IRedisSchedulerService _redisScheduler = RedisScheduler;
    private readonly IRedisStatsService _statsService = StatsService;
    private readonly IJobOccurrenceEventPublisher _eventPublisher = EventPublisher;
    private readonly IMilvaLogger _logger = Logger;
    private readonly IHttpContextAccessor _httpContextAccessor = HttpContextAccessor;

    private const string PublishFailedMessage = "The job could not be queued because the message broker is unavailable. Please try again shortly.";

    /// <inheritdoc/>
    public async Task<Response<Guid>> Handle(TriggerScheduledJobCommand request, CancellationToken cancellationToken)
    {
        // 1. Get the job
        var job = await _jobRepository.GetByIdAsync(request.JobId, cancellationToken: cancellationToken);

        if (job == null)
            return Response<Guid>.Error(default, "Job not found");

        // External jobs cannot be triggered from Milvaion - they are managed by their own schedulers
        if (job.IsExternal)
            return Response<Guid>.Error(default, "External jobs cannote be triggered!");

        if (!job.IsActive)
            return Response<Guid>.Error(default, "Job is not active");

        // 2. Check for running occurrences using Redis (atomic check for ConcurrentPolicy enforcement)
        if (!request.Force) // Only check if not force trigger
        {
            // Use Redis for atomic running check (prevents race conditions)
            var isRunning = await _redisScheduler.IsJobRunningAsync(job.Id, cancellationToken);

            // Apply ConcurrentExecutionPolicy.Skip check
            if (job.ConcurrentExecutionPolicy == ConcurrentExecutionPolicy.Skip && isRunning)
                return Response<Guid>.Error(default, "Job already has a running occurrence. ConcurrentExecutionPolicy is set to Skip. Please wait for the current execution to complete or use Force trigger.");
        }

        // 3. Create a new occurrence for manual execution
        var occurrenceId = Guid.CreateVersion7();

        var occurrence = new JobOccurrence
        {
            Id = occurrenceId,
            JobId = job.Id,
            JobName = job.JobNameInWorker,
            ZombieTimeoutMinutes = job.ZombieTimeoutMinutes,
            JobVersion = job.Version,
            CorrelationId = occurrenceId, // For manual triggers, same as occurrence ID
            Status = JobOccurrenceStatus.Queued,
            CreatedAt = DateTime.UtcNow,
            Logs =
            [
                new JobOccurrenceLog
                {
                    Id = Guid.CreateVersion7(),
                    Timestamp = DateTime.UtcNow,
                    Level = request.Force ? "Warning" : "Information",
                    Message = request.Force
                        ? $"Job FORCE triggered (bypassing policy): {request.Reason ?? "Manual execution by admin"}"
                        : $"Job manually triggered: {request.Reason ?? $"Manual execution by {_httpContextAccessor.HttpContext?.CurrentUserName() ?? "Anonymous"}"}",
                    Category = "ManualTrigger",
                    Data = new Dictionary<string, object>
                    {
                        ["JobId"] = job.Id.ToString(),
                        ["JobType"] = job.JobNameInWorker,
                        ["TriggeredBy"] = request.Force ? "Admin (Force)" : "User",
                        ["Reason"] = request.Reason ?? "Manual trigger",
                        ["Force"] = request.Force
                    }
                }
            ]
        };

        if (!string.IsNullOrWhiteSpace(request.JobData))
        {
            job.JobData = ScheduledJob.FixJobData(request.JobData);

            occurrence.Logs[0].Message += $". With custom JobData provided.";
            // Only the fact: job data can hold secrets and logs are widely readable.
            occurrence.Logs[0].Data.Add("HasCustomJobData", true);
        }

        await _occurrenceRepository.AddAsync(occurrence, cancellationToken: cancellationToken);

        // Update stats counters (new Queued occurrence)
        try
        {
            await _statsService.IncrementTotalOccurrencesAsync(CancellationToken.None);
            await _statsService.IncrementStatusCounterAsync(JobOccurrenceStatus.Queued, CancellationToken.None);
            await _statsService.TrackExecutionAsync(occurrence.Id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to update statistics for triggered occurrence {OccurrenceId}", occurrence.Id);
        }

        // Publish SignalR event for occurrence created
        await _eventPublisher.PublishOccurrenceCreatedAsync([occurrence], _logger, cancellationToken);

        // 4. Publish to RabbitMQ immediately (bypass ExecuteAt check)
        bool published;
        var failureMessage = PublishFailedMessage;

        try
        {
            published = await _rabbitMQPublisher.PublishJobAsync(job, occurrenceId, cancellationToken);
        }
        catch (SecretResolutionException ex)
        {
            // Nothing was published. The message names the missing secrets, never values.
            published = false;
            failureMessage = ex.Message;
        }

        if (!published)
        {
            var now = DateTime.UtcNow;

            occurrence.StatusChangeLogs.Add(new OccurrenceStatusChangeLog
            {
                Timestamp = now,
                From = JobOccurrenceStatus.Queued,
                To = JobOccurrenceStatus.Failed
            });

            /*
             * A conditional update rather than UpdateAsync on the tracked occurrence: adding a log with a preset id to
             * its Logs collection made EF treat the log as an existing row, so the save threw a concurrency exception,
             * the caller got a 500 and the occurrence stayed Queued.
             */
            var setPropertyBuilder = new SetPropertyBuilder<JobOccurrence>().SetPropertyValue(o => o.Status, JobOccurrenceStatus.Failed)
                                                                            .SetPropertyValue(o => o.Exception, failureMessage)
                                                                            .SetPropertyValue(o => o.EndTime, now)
                                                                            .SetPropertyValue(o => o.StatusChangeLogs, occurrence.StatusChangeLogs);

            var affected = await _occurrenceRepository.ExecuteUpdateAsync(o => o.Id == occurrenceId && o.Status == JobOccurrenceStatus.Queued, setPropertyBuilder, cancellationToken: CancellationToken.None);

            await _occurrenceLogRepository.AddAsync(new JobOccurrenceLog
            {
                Id = Guid.CreateVersion7(),
                OccurrenceId = occurrenceId,
                Timestamp = now,
                Level = "Error",
                Message = failureMessage,
                Category = "ManualTrigger"
            }, cancellationToken: CancellationToken.None);

            if (affected > 0)
            {
                try
                {
                    await _statsService.UpdateStatusCountersAsync(JobOccurrenceStatus.Queued, JobOccurrenceStatus.Failed, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to update status counters for occurrence {OccurrenceId}", occurrenceId);
                }
            }

            return Response<Guid>.Error(default, failureMessage);
        }

        return Response<Guid>.Success(occurrenceId, "Job triggered successfully");
    }
}
