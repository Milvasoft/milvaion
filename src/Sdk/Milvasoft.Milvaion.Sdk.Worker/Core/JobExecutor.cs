using Microsoft.Extensions.Logging;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Models;
using Milvasoft.Milvaion.Sdk.Utils;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Milvasoft.Milvaion.Sdk.Worker.Exceptions;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using Milvasoft.Milvaion.Sdk.Worker.Persistence;
using System.Diagnostics;

namespace Milvasoft.Milvaion.Sdk.Worker.Core;

/// <summary>
/// Executes jobs with error handling, metrics collection, and logging.
/// </summary>
public class JobExecutor(ILoggerFactory loggerFactory)
{
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<IMilvaLogger>();

    /// <summary>
    /// Executes a job with full error handling and metrics.
    /// </summary>
    /// <param name="job">The job instance to execute</param>
    /// <param name="scheduledJob">Scheduled job definition</param>
    /// <param name="correlationId">Correlation ID for tracing</param>
    /// <param name="outboxService">Outbox service for resilient logging</param>
    /// <param name="workerOptions">Interval for sending job heartbeats (0 to disable)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Job execution result with metrics</returns>
    public async Task<JobExecutionResult> ExecuteAsync(IJobBase job,
                                                       ScheduledJob scheduledJob,
                                                       Guid correlationId,
                                                       OutboxService outboxService,
                                                       WorkerOptions workerOptions,
                                                       JobConsumerConfig executedJobConsumerConfig,
                                                       CancellationToken cancellationToken)
    {

        // Timeout priority: ScheduledJob > JobConsumerConfig > WorkerOptions
        var timeoutSeconds = scheduledJob.ExecutionTimeoutSeconds  // 1. Job-specific
                             ?? executedJobConsumerConfig?.ExecutionTimeoutSeconds  // 2. Job-type (worker config)
                             ?? workerOptions.ExecutionTimeoutSeconds;  // 3. Global default

        var stopwatch = Stopwatch.StartNew();
        var startTime = DateTime.UtcNow;

        // Get job heartbeat interval from options
        var jobHeartbeatIntervalSeconds = workerOptions.Heartbeat?.JobHeartbeatIntervalSeconds ?? 60;

        // Create job context with OutboxService for resilient logging
        var context = new JobContext(correlationId, scheduledJob, workerOptions.InstanceId, _logger, outboxService, executedJobConsumerConfig, cancellationToken);

        _logger.Debug($"[DEBUG] JobExecutor creating context for job {scheduledJob.JobNameInWorker}");

        // Create timeout cancellation token source if timeout is configured
        CancellationTokenSource timeoutCts = null;
        CancellationTokenSource linkedCts = null;
        CancellationToken executionToken = cancellationToken;
        CancellationTokenSource heartbeatCts = null;
        Task heartbeatTask = null;

        try
        {
            // Setup timeout if configured (executionTimeoutSeconds > 0)
            if (timeoutSeconds > 0)
            {
                timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                executionToken = linkedCts.Token;

                // Reconfigure context with timeout-aware cancellation token
                context.ReconfigureTimeout(executedJobConsumerConfig, executionToken);
            }

            // Start periodic heartbeat task if outbox service is available and interval is configured
            if (outboxService != null && jobHeartbeatIntervalSeconds > 0)
            {
                heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(executionToken);
                heartbeatTask = RunJobHeartbeatAsync(outboxService, correlationId, scheduledJob.Id, workerOptions.InstanceId, jobHeartbeatIntervalSeconds, heartbeatCts.Token);
            }

            // Execute the job with timeout-aware cancellation token
            var result = await InvokeJobAsync(job, context);

            stopwatch.Stop();

            var endTime = DateTime.UtcNow;

            return new JobExecutionResult
            {
                CorrelationId = correlationId,
                JobId = scheduledJob.Id,
                WorkerId = workerOptions.InstanceId,
                Status = JobOccurrenceStatus.Completed,
                StartTime = startTime,
                EndTime = endTime,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Result = result ?? $"Job {scheduledJob.JobNameInWorker} completed successfully",
                Logs = context.GetLogs()
            };
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true)
        {
            // Job was cancelled due to timeout
            stopwatch.Stop();

            var endTime = DateTime.UtcNow;

            context.LogError($"Job execution exceeded timeout limit of {timeoutSeconds} seconds");

            return new JobExecutionResult
            {
                CorrelationId = correlationId,
                JobId = scheduledJob.Id,
                WorkerId = workerOptions.InstanceId,
                Status = JobOccurrenceStatus.TimedOut,
                StartTime = startTime,
                EndTime = endTime,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Exception = $"Job execution exceeded maximum timeout of {timeoutSeconds} seconds ({stopwatch.ElapsedMilliseconds}ms elapsed)",
                Logs = context.GetLogs()
            };
        }
        catch (OperationCanceledException)
        {
            // Job was cancelled by user or system (not timeout)
            stopwatch.Stop();

            var endTime = DateTime.UtcNow;

            context.LogWarning("Job execution was cancelled");

            return new JobExecutionResult
            {
                CorrelationId = correlationId,
                JobId = scheduledJob.Id,
                WorkerId = workerOptions.InstanceId,
                Status = JobOccurrenceStatus.Cancelled,
                StartTime = startTime,
                EndTime = endTime,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Result = "Job cancelled by user or system",
                Logs = context.GetLogs()
            };
        }
        catch (PermanentJobException ex)
        {
            // Permanent failure - should NOT be retried, go directly to DLQ
            stopwatch.Stop();

            var endTime = DateTime.UtcNow;

            context.LogError($"Permanent job failure (will not retry): {ex.Message}", ex);

            return new JobExecutionResult
            {
                CorrelationId = correlationId,
                JobId = scheduledJob.Id,
                WorkerId = workerOptions.InstanceId,
                Status = JobOccurrenceStatus.Failed,
                StartTime = startTime,
                EndTime = endTime,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Exception = FormatException(ex),
                IsPermanentFailure = true,
                Logs = context.GetLogs()
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            var endTime = DateTime.UtcNow;

            context.LogError($"Job execution failed: {ex.Message}", ex);

            return new JobExecutionResult
            {
                CorrelationId = correlationId,
                JobId = scheduledJob.Id,
                WorkerId = workerOptions.InstanceId,
                Status = JobOccurrenceStatus.Failed,
                StartTime = startTime,
                EndTime = endTime,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Exception = FormatException(ex),
                IsPermanentFailure = false,
                Logs = context.GetLogs()
            };
        }
        finally
        {
            // Stop heartbeat task
            if (heartbeatCts != null)
            {
                await heartbeatCts.CancelAsync();
                heartbeatCts.Dispose();

                // Wait for heartbeat task to complete (with short timeout)
                if (heartbeatTask != null)
                {
                    try
                    {
                        await heartbeatTask.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
                    }
                    catch (OperationCanceledException) { }
                    catch (TimeoutException) { }
                }
            }

            // Cleanup timeout resources
            timeoutCts?.Dispose();
            linkedCts?.Dispose();
        }
    }

    /// <summary>
    /// Runs periodic heartbeat for a job to prevent zombie detection.
    /// </summary>
    private async Task RunJobHeartbeatAsync(OutboxService outboxService,
                                            Guid correlationId,
                                            Guid jobId,
                                            string workerId,
                                            int intervalSeconds,
                                            CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken);

                await outboxService.PublishJobHeartbeatAsync(correlationId, jobId, workerId, CancellationToken.None);

                _logger.Debug("Job heartbeat sent for CorrelationId {CorrelationId}", correlationId);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when job completes
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Job heartbeat task ended unexpectedly for CorrelationId {CorrelationId}", correlationId);
        }
    }

    /// <summary>
    /// Formats exception details for storage.
    /// </summary>
    private static string FormatException(Exception ex)
    {
        var formatted = $"Type: {ex.GetType().FullName}\n";

        formatted += $"Message: {ex.Message}\n";
        formatted += $"StackTrace: {ex.StackTrace}\n";

        if (ex.InnerException != null)
            formatted += $"\nInner Exception:\n{FormatException(ex.InnerException)}";

        return formatted;
    }

    private static async Task<string> InvokeJobAsync(object jobInstance, JobContext context)
    {
        if (jobInstance is IAsyncJobWithResult asyncWithResult)
        {
            return await asyncWithResult.ExecuteAsync(context);
        }

        if (jobInstance is IAsyncJob asyncJob)
        {
            await asyncJob.ExecuteAsync(context);
            return null;
        }

        if (jobInstance is IJobWithResult syncWithResult)
        {
            return await Task.Run(() => syncWithResult.Execute(context), context.CancellationToken);
        }

        if (jobInstance is IJob syncJob)
        {
            await Task.Run(() => syncJob.Execute(context), context.CancellationToken);
            return null;
        }

        throw new InvalidOperationException($"Unsupported job type: {jobInstance?.GetType().FullName}");
    }
}
