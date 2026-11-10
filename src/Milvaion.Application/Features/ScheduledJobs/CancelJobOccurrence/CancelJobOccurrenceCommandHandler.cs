using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Milvaion.Application.Interfaces.Redis;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.DataAccess.EfCore.Utils;
using Milvasoft.Interception.Ef.Transaction;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.ScheduledJobs.CancelJobOccurrence;

/// <summary>
/// Handles job occurrence cancellation by publishing cancellation signal to Redis Pub/Sub.
/// </summary>
[Log]
[Transaction]
[UserActivityTrack(UserActivity.CancelJobOccurrence)]
public record CancelJobOccurrenceCommandHandler(IMilvaionRepositoryBase<JobOccurrence> OccurrenceRepository,
                                                IMilvaionRepositoryBase<JobOccurrenceLog> OccurrenceLogRepository,
                                                IJobCancellationService CancellationService,
                                                IRedisSchedulerService SchedulerService,
                                                IRedisStatsService StatsService,
                                                IHttpContextAccessor HttpContextAccessor,
                                                IMilvaLogger Logger) : IInterceptable, ICommandHandler<CancelJobOccurrenceCommand, bool>
{
    private readonly IMilvaionRepositoryBase<JobOccurrence> _occurenceRepository = OccurrenceRepository;
    private readonly IMilvaionRepositoryBase<JobOccurrenceLog> _occurenceLogRepository = OccurrenceLogRepository;
    private readonly IJobCancellationService _cancellationService = CancellationService;
    private readonly IRedisSchedulerService _schedulerService = SchedulerService;
    private readonly IRedisStatsService _statsService = StatsService;
    private readonly IHttpContextAccessor _httpContextAccessor = HttpContextAccessor;
    private readonly IMilvaLogger _logger = Logger;

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

        var now = DateTime.UtcNow;

        occurrence.StatusChangeLogs.Add(new OccurrenceStatusChangeLog
        {
            Timestamp = now,
            From = occurrence.Status,
            To = JobOccurrenceStatus.Cancelled
        });

        var message = $"Job cancelled by {_httpContextAccessor.HttpContext?.CurrentUserName() ?? "Anonymous"} . Reason : {request.Reason ?? "No Reason"}";

        var setPropertyBuilder = new SetPropertyBuilder<JobOccurrence>().SetPropertyValue(o => o.Status, JobOccurrenceStatus.Cancelled)
                                                                        .SetPropertyValue(o => o.Exception, message)
                                                                        .SetPropertyValue(o => o.EndTime, now)
                                                                        .SetPropertyValue(o => o.StatusChangeLogs, occurrence.StatusChangeLogs);

        if (occurrence.StartTime.HasValue)
            setPropertyBuilder = setPropertyBuilder.SetPropertyValue(o => o.DurationMs, (long)(now - occurrence.StartTime.Value).TotalMilliseconds);

        // Conditional, because the worker may report a final status at any moment and that must not be overwritten.
        var affected = await _occurenceRepository.ExecuteUpdateAsync(o => o.Id == occurrence.Id && o.Status == JobOccurrenceStatus.Running, setPropertyBuilder, cancellationToken: cancellationToken);

        if (affected == 0)
            return Response<bool>.Error(false, "Occurrence is no longer running");

        await _occurenceLogRepository.AddAsync(new JobOccurrenceLog
        {
            Id = Guid.CreateVersion7(),
            OccurrenceId = occurrence.Id,
            Timestamp = now,
            Level = LogLevel.Warning.ToString(),
            Message = message,
            Category = "Cancellation"
        }, cancellationToken: cancellationToken);

        // Workers track a run by occurrence id. The correlation id is shared by every step of a workflow run.
        var published = await _cancellationService.PublishCancellationAsync(occurrence.Id,
                                                                            occurrence.JobId,
                                                                            occurrence.Id,
                                                                            request.Reason ?? MessageConstant.CancelledByUser,
                                                                            cancellationToken);

        // Mark job as completed in Redis (remove from running set)
        await _schedulerService.MarkJobAsCompletedAsync(occurrence.JobId, cancellationToken);

        // Dashboard counters are not worth failing a cancellation that has already happened.
        try
        {
            await _statsService.UpdateStatusCountersAsync(JobOccurrenceStatus.Running, JobOccurrenceStatus.Cancelled, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to update status counters for cancelled occurrence {OccurrenceId}", occurrence.Id);
        }

        return Response<bool>.Success(true, $"Cancellation signal sent to {published} worker(s)");
    }
}
