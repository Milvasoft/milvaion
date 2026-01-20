using Microsoft.AspNetCore.Http;
using Milvaion.Application.Interfaces.RabbitMQ;
using Milvaion.Application.Interfaces.Redis;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.ScheduledJobs.TriggerScheduledJob;

/// <summary>
/// Handles manual job triggering by creating occurrence and dispatching immediately.
/// </summary>
[Log]
[UserActivityTrack(UserActivity.CreateScheduledJob)] // Reuse existing activity
public record TriggerScheduledJobCommandHandler(IMilvaionRepositoryBase<ScheduledJob> JobRepository,
                                                IMilvaionRepositoryBase<JobOccurrence> OccurrenceRepository,
                                                IRabbitMQPublisher RabbitMQPublisher,
                                                IRedisSchedulerService RedisScheduler,
                                                IJobOccurrenceEventPublisher EventPublisher,
                                                IMilvaLogger Logger,
                                                IHttpContextAccessor HttpContextAccessor) : IInterceptable, ICommandHandler<TriggerScheduledJobCommand, Guid>
{
    private readonly IMilvaionRepositoryBase<ScheduledJob> _jobRepository = JobRepository;
    private readonly IMilvaionRepositoryBase<JobOccurrence> _occurrenceRepository = OccurrenceRepository;
    private readonly IRabbitMQPublisher _rabbitMQPublisher = RabbitMQPublisher;
    private readonly IRedisSchedulerService _redisScheduler = RedisScheduler;
    private readonly IJobOccurrenceEventPublisher _eventPublisher = EventPublisher;
    private readonly IMilvaLogger _logger = Logger;
    private readonly IHttpContextAccessor _httpContextAccessor = HttpContextAccessor;

    /// <inheritdoc/>
    public async Task<Response<Guid>> Handle(TriggerScheduledJobCommand request, CancellationToken cancellationToken)
    {
        // 1. Get the job
        var job = await _jobRepository.GetByIdAsync(request.JobId, cancellationToken: cancellationToken);

        if (job == null)
            return Response<Guid>.Error(default, "Job not found");

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
        var correlationId = Guid.CreateVersion7();

        var occurrence = new JobOccurrence
        {
            Id = correlationId,
            JobId = job.Id,
            JobName = job.JobNameInWorker,
            ZombieTimeoutMinutes = job.ZombieTimeoutMinutes,
            JobVersion = job.Version,
            CorrelationId = correlationId,
            Status = JobOccurrenceStatus.Queued,
            CreatedAt = DateTime.UtcNow,
            Logs =
            [
                new OccurrenceLog
                {
                    Timestamp = DateTime.UtcNow,
                    Level = request.Force ? "Warning" : "Information",
                    Message = request.Force
                        ? $"Job FORCE triggered (bypassing policy): {request.Reason ?? "Manual execution by admin"}"
                        : $"Job manually triggered: {request.Reason ?? "Manual execution by " + _httpContextAccessor.HttpContext?.CurrentUserName() ?? "Anonymous"}",
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
            occurrence.Logs[0].Data.Add("CustomJobData", request.JobData);
        }

        await _occurrenceRepository.AddAsync(occurrence, cancellationToken: cancellationToken);

        // Publish SignalR event for occurrence created
        await _eventPublisher.PublishOccurrenceCreatedAsync([occurrence], _logger, cancellationToken);

        // 4. Publish to RabbitMQ immediately (bypass ExecuteAt check)
        var published = await _rabbitMQPublisher.PublishJobAsync(job, correlationId, cancellationToken);

        if (!published)
        {
            occurrence.Status = JobOccurrenceStatus.Failed;
            occurrence.Exception = "Failed to publish to RabbitMQ";
            occurrence.Logs.Add(new OccurrenceLog
            {
                Timestamp = DateTime.UtcNow,
                Level = "Error",
                Message = "Failed to publish job to RabbitMQ queue",
                Category = "ManualTrigger"
            });

            await _occurrenceRepository.UpdateAsync(occurrence, cancellationToken: cancellationToken);

            return Response<Guid>.Error(default, "Failed to publish job to RabbitMQ");
        }

        return Response<Guid>.Success(correlationId, "Job triggered successfully");
    }
}
