using Hangfire.Client;
using Hangfire.Server;
using Hangfire.States;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Domain.JsonModels;
using Milvasoft.Milvaion.Sdk.Utils;
using Milvasoft.Milvaion.Sdk.Worker.Hangfire.Extensions;
using Milvasoft.Milvaion.Sdk.Worker.Hangfire.Services;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using Milvasoft.Milvaion.Sdk.Worker.Utils;
using System.Globalization;

namespace Milvasoft.Milvaion.Sdk.Worker.Hangfire.Filters;

/// <summary>
/// Hangfire filter that intercepts job executions and reports them to Milvaion.
/// Implements IServerFilter for job execution lifecycle and IElectStateFilter for state changes.
/// All methods are wrapped in try-catch to ensure Milvaion integration never affects Hangfire operation.
/// </summary>
public class MilvaionJobFilter(IExternalJobPublisher publisher,
                               IOptions<WorkerOptions> workerOptions,
                               ExternalJobRegistry jobRegistry,
                               ILoggerFactory loggerFactory) : IServerFilter, IElectStateFilter, IClientFilter
{
    private readonly IExternalJobPublisher _publisher = publisher;
    private readonly MilvaionExternalSchedulerOptions _options = workerOptions.Value.ExternalScheduler;
    private readonly WorkerOptions _workerOptions = workerOptions?.Value;
    private readonly ExternalJobRegistry _jobRegistry = jobRegistry;
    private readonly IMilvaLogger _logger = loggerFactory?.CreateMilvaLogger<MilvaionJobFilter>();
    private const string _correlationIdKey = "Milvaion_CorrelationId";
    private const string _workerIdKey = "Milvaion_WorkerId";
    private const string _startTimeKey = "Milvaion_StartTime";

    #region IClientFilter - Job Creation

    /// <summary>
    /// Called when a job is being created. Registers the job with Milvaion.
    /// </summary>
    public void OnCreating(CreatingContext context)
    {
        try
        {
            if (_publisher == null || _options == null)
                return;

            var jobType = context.Job?.Type;
            var methodName = context.Job?.Method?.Name ?? "Execute";

            if (jobType == null)
                return;

            var externalJobId = jobType.GetExternalJobId(methodName);

            // Register job in local registry
            _jobRegistry?.RegisterJob(externalJobId, jobType);
        }
        catch (Exception ex)
        {
            LogSafeError(ex, "OnCreating", context?.Job?.Type?.Name);
        }
    }

    /// <summary>
    /// Called after a job is created.
    /// </summary>
    public void OnCreated(CreatedContext context)
    {
        try
        {
            if (_publisher == null || _options == null)
                return;

            var jobType = context.Job?.Type;

            if (jobType == null)
                return;

            var methodName = context.Job?.Method?.Name ?? "Execute";
            var externalJobId = jobType.GetExternalJobId(methodName);

            // Publish registration message to Milvaion
            var message = new ExternalJobRegistrationMessage
            {
                ExternalJobId = externalJobId,
                Source = _options.Source,
                DisplayName = $"{jobType.Name}.{methodName}",
                Description = $"Hangfire job: {jobType.FullName}.{methodName}",
                JobTypeName = jobType.FullName ?? jobType.Name,
                WorkerId = _workerOptions?.WorkerId,
                IsActive = true
            };

            Task.Run(async () =>
            {
                try
                {
                    await _publisher.PublishJobRegistrationAsync(message);
                    _logger?.Debug("Registered Hangfire job with Milvaion: {ExternalJobId}", externalJobId);
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Failed to publish job registration for {ExternalJobId}", externalJobId);
                }
            });
        }
        catch (Exception ex)
        {
            LogSafeError(ex, "OnCreated", context?.Job?.Type?.Name);
        }
    }

    #endregion

    #region IServerFilter - Job Execution

    /// <summary>
    /// Called before a job is executed. Creates a new occurrence in Milvaion.
    /// </summary>
    public void OnPerforming(PerformingContext context)
    {
        try
        {
            if (_publisher == null || _options == null)
                return;

            var correlationId = Guid.CreateVersion7();
            var startTime = DateTime.UtcNow;

            // Store correlation ID in job parameters for later use
            context.SetJobParameter(_correlationIdKey, correlationId.ToString());
            context.SetJobParameter(_workerIdKey, _workerOptions?.WorkerId ?? "unknown");
            context.SetJobParameter(_startTimeKey, startTime.ToString("O"));

            var jobType = context.BackgroundJob.Job.Type;
            var methodName = context.BackgroundJob.Job.Method.Name;
            var externalJobId = jobType.GetExternalJobId(methodName);

            var message = new ExternalJobOccurrenceMessage
            {
                CorrelationId = correlationId,
                ExternalJobId = externalJobId,
                ExternalOccurrenceId = context.BackgroundJob.Id,
                Source = _options.Source,
                JobTypeName = jobType.FullName ?? jobType.Name,
                EventType = ExternalOccurrenceEventType.Starting,
                WorkerId = _workerOptions?.WorkerId,
                Status = JobOccurrenceStatus.Running,
                StartTime = startTime
            };

            // Publish asynchronously (fire and forget in filter context)
            Task.Run(async () =>
            {
                try
                {
                    await _publisher.PublishOccurrenceEventAsync(message);
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Failed to publish job starting event for {JobId}", context.BackgroundJob.Id);
                }
            });

            _logger?.Debug("Job starting: {JobType}.{Method}, CorrelationId: {CorrelationId}", jobType.Name, methodName, correlationId);
        }
        catch (Exception ex)
        {
            LogSafeError(ex, "OnPerforming", context?.BackgroundJob?.Job?.Type?.Name);
        }
    }

    /// <summary>
    /// Called after a job has been executed. Updates the occurrence with final status.
    /// </summary>
    public void OnPerformed(PerformedContext context)
    {
        try
        {
            if (_publisher == null || _options == null)
                return;

            var correlationIdStr = context.GetJobParameter<string>(_correlationIdKey);
            var startTimeStr = context.GetJobParameter<string>(_startTimeKey);
            var correlationId = Guid.TryParse(correlationIdStr, out var cid) ? cid : Guid.CreateVersion7();

            var endTime = DateTime.UtcNow;

            // RoundtripKind keeps the stored UTC value; the default would convert it to local time and skew the duration by the UTC offset
            var startTime = DateTime.TryParse(startTimeStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var st) ? st : endTime;
            var durationMs = (long)(endTime - startTime).TotalMilliseconds;

            var status = context.Exception == null ? JobOccurrenceStatus.Completed : JobOccurrenceStatus.Failed;

            var jobType = context.BackgroundJob.Job.Type;
            var methodName = context.BackgroundJob.Job.Method.Name;
            var externalJobId = jobType.GetExternalJobId(methodName);

            var message = new ExternalJobOccurrenceMessage
            {
                CorrelationId = correlationId,
                ExternalJobId = externalJobId,
                ExternalOccurrenceId = context.BackgroundJob.Id,
                Source = _options.Source,
                JobTypeName = jobType.FullName ?? jobType.Name,
                EventType = ExternalOccurrenceEventType.Completed,
                WorkerId = _workerOptions?.WorkerId,
                Status = status,
                EndTime = endTime,
                DurationMs = durationMs,
                Result = context.Exception == null ? $"Job {externalJobId} completed successfully" : null,
                Exception = context.Exception?.ToString()
            };

            // Publish asynchronously (fire and forget in filter context)
            Task.Run(async () =>
            {
                try
                {
                    await _publisher.PublishOccurrenceEventAsync(message);
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Failed to publish job completed event for {JobId}", context.BackgroundJob.Id);
                }
            });

            _logger?.Debug("Job completed: {JobType}.{Method}, Status: {Status}, Duration: {Duration}ms, CorrelationId: {CorrelationId}",
                jobType.Name, methodName, status, durationMs, correlationId);
        }
        catch (Exception ex)
        {
            LogSafeError(ex, "OnPerformed", context?.BackgroundJob?.Job?.Type?.Name);
        }
    }

    #endregion

    #region IElectStateFilter - State Changes

    /// <summary>
    /// Called when a job's state is about to change.
    /// Used to track job cancellations and other state transitions.
    /// </summary>
    public void OnStateElection(ElectStateContext context)
    {
        try
        {
            if (_publisher == null || _options == null)
                return;

            // Track deletion/cancellation
            if (context.CandidateState is DeletedState)
            {
                // A job deleted before it ever ran has no occurrence in Milvaion to cancel
                if (!Guid.TryParse(context.GetJobParameter<string>(_correlationIdKey), out var correlationId))
                    return;

                var jobType = context.BackgroundJob.Job.Type;
                var methodName = context.BackgroundJob.Job.Method.Name;
                var externalJobId = jobType.GetExternalJobId(methodName);

                var message = new ExternalJobOccurrenceMessage
                {
                    CorrelationId = correlationId,
                    ExternalJobId = externalJobId,
                    ExternalOccurrenceId = context.BackgroundJob.Id,
                    Source = _options.Source,
                    JobTypeName = jobType.FullName ?? jobType.Name,
                    EventType = ExternalOccurrenceEventType.Cancelled,
                    WorkerId = _workerOptions?.WorkerId,
                    Status = JobOccurrenceStatus.Cancelled,
                    EndTime = DateTime.UtcNow,
                    Result = "Job was deleted/cancelled"
                };

                Task.Run(async () =>
                {
                    try
                    {
                        await _publisher.PublishOccurrenceEventAsync(message);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Error(ex, "Failed to publish job cancelled event for {JobId}", context.BackgroundJob.Id);
                    }
                });

                _logger?.Debug("Job cancelled: {JobType}.{Method}, CorrelationId: {CorrelationId}", jobType.Name, methodName, correlationId);
            }
        }
        catch (Exception ex)
        {
            LogSafeError(ex, "OnStateElection", context?.BackgroundJob?.Job?.Type?.Name);
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Safely logs an error without throwing.
    /// </summary>
    private void LogSafeError(Exception ex, string methodName, string context = null)
    {
        try
        {
            if (string.IsNullOrEmpty(context))
                _logger?.Error(ex, "[Milvaion] Error in {Method} - integration continues silently", methodName);
            else
                _logger?.Error(ex, "[Milvaion] Error in {Method} for {Context} - integration continues silently", methodName, context);
        }
        catch
        {
            // Even logging failed - silently continue
        }
    }

    #endregion
}
