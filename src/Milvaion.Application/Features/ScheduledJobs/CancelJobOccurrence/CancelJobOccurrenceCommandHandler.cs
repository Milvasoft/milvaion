using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Milvaion.Application.Interfaces.Redis;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.ScheduledJobs.CancelJobOccurrence;

/// <summary>
/// Handles job occurrence cancellation by publishing cancellation signal to Redis Pub/Sub.
/// </summary>
[Log]
[UserActivityTrack(UserActivity.CancelJobOccurrence)]
public record CancelJobOccurrenceCommandHandler(IMilvaionRepositoryBase<JobOccurrence> OccurrenceRepository,
                                                IMilvaionRepositoryBase<JobOccurrenceLog> OccurrenceLogRepository,
                                                IJobCancellationService CancellationService,
                                                IRedisSchedulerService SchedulerService,
                                                IRedisStatsService StatsService,
                                                IHttpContextAccessor HttpContextAccessor) : IInterceptable, ICommandHandler<CancelJobOccurrenceCommand, bool>
{
    private readonly IMilvaionRepositoryBase<JobOccurrence> _occurenceRepository = OccurrenceRepository;
    private readonly IMilvaionRepositoryBase<JobOccurrenceLog> _occurenceLogRepository = OccurrenceLogRepository;
    private readonly IJobCancellationService _cancellationService = CancellationService;
    private readonly IRedisSchedulerService _schedulerService = SchedulerService;
    private readonly IRedisStatsService _statsService = StatsService;
    private readonly IHttpContextAccessor _httpContextAccessor = HttpContextAccessor;

    /// <inheritdoc/>
    public async Task<Response<bool>> Handle(CancelJobOccurrenceCommand request, CancellationToken cancellationToken)
    {
        // Find the running occurrence by CorrelationId
        var occurrence = await _occurenceRepository.GetFirstOrDefaultAsync(o => o.Id == request.OccurrenceId, cancellationToken: cancellationToken);

        if (occurrence == null)
            return Response<bool>.Error(false, "Occurrence not found");

        // External job occurrences cannot be cancelled from Milvaion - they are managed by their own schedulers
        if (!string.IsNullOrWhiteSpace(occurrence.ExternalJobId))
            return Response<bool>.Error(false, MessageKey.ExternalJobCannotBeCancelled);

        if (occurrence.Status != JobOccurrenceStatus.Running)
            return Response<bool>.Error(false, $"Occurrence is not running (Status: {occurrence.Status})");

        // Publish cancellation signal via service
        var published = await _cancellationService.PublishCancellationAsync(occurrence.CorrelationId,
                                                                            occurrence.JobId,
                                                                            occurrence.Id,
                                                                            request.Reason ?? MessageConstant.CancelledByUser,
                                                                            cancellationToken);

        // Update occurrence status
        occurrence.StatusChangeLogs.Add(new OccurrenceStatusChangeLog
        {
            Timestamp = DateTime.UtcNow,
            From = occurrence.Status,
            To = JobOccurrenceStatus.Cancelled
        });

        var message = $"Job cancelled by {_httpContextAccessor.HttpContext?.CurrentUserName() ?? "Anonymous"} . Reason : {request.Reason ?? "No Reason"}";

        occurrence.Status = JobOccurrenceStatus.Cancelled;
        occurrence.Exception = message;
        occurrence.EndTime = DateTime.UtcNow;

        if (occurrence.StartTime.HasValue)
            occurrence.DurationMs = (int)(DateTime.UtcNow - occurrence.StartTime.Value).TotalMilliseconds;

        var log = new JobOccurrenceLog
        {
            Id = Guid.CreateVersion7(),
            OccurrenceId = occurrence.Id,
            Timestamp = DateTime.UtcNow,
            Level = LogLevel.Warning.ToString(),
            Message = message,
            Category = "Cancellation"
        };

        await _occurenceRepository.UpdateAsync(occurrence, cancellationToken: cancellationToken);
        await _occurenceLogRepository.AddAsync(log, cancellationToken: cancellationToken);

        // Mark job as completed in Redis (remove from running set)
        await _schedulerService.MarkJobAsCompletedAsync(occurrence.JobId, cancellationToken);

        // Update stats counters (Running -> Cancelled)
        _ = Task.Run(async () =>
        {
            try
            {
                await _statsService.UpdateStatusCountersAsync(JobOccurrenceStatus.Running, JobOccurrenceStatus.Cancelled, cancellationToken);
            }
            catch
            {
                // Non-critical
            }
        }, CancellationToken.None);

        return Response<bool>.Success(true, $"Cancellation signal sent to {published} worker(s)");
    }
}
