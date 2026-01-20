using Microsoft.Extensions.Logging;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using Milvasoft.Milvaion.Sdk.Worker.Persistence;
using System.Text.Json;

namespace Milvasoft.Milvaion.Sdk.Worker.Core;

/// <summary>
/// Implementation of IJobContext providing job execution environment.
/// </summary>
public class JobContext(Guid correlationId,
                        ScheduledJob job,
                        string workerId,
                        IMilvaLogger logger,
                        OutboxService outboxService,
                        JobConsumerConfig jobConsumerConfig,
                        CancellationToken cancellationToken) : IJobContext
{
    private readonly List<OccurrenceLog> _logs = [];
    private readonly Lock _logLock = new();
    private readonly OutboxService _outboxService = outboxService;

    /// <inheritdoc/>
    public Guid CorrelationId { get; private set; } = correlationId;

    /// <inheritdoc/>
    public ScheduledJob Job { get; private set; } = job;

    /// <inheritdoc/>
    public string WorkerId { get; private set; } = workerId;

    /// <inheritdoc/>
    public CancellationToken CancellationToken { get; private set; } = cancellationToken;

    /// <inheritdoc/>
    public IMilvaLogger Logger { get; private set; } = logger;

    /// <inheritdoc/>
    public JobConsumerConfig ExecutorJobConsumerConfig { get; private set; } = jobConsumerConfig;

    /// <summary>
    /// Reconfigures the job context with new timeout and cancellation token.
    /// </summary>
    /// <param name="jobConsumerConfig"></param>
    /// <param name="cancellationToken"></param>
    public void ReconfigureTimeout(JobConsumerConfig jobConsumerConfig, CancellationToken cancellationToken)
    {
        CancellationToken = cancellationToken;
        ExecutorJobConsumerConfig = jobConsumerConfig;
    }

    /// <inheritdoc/>
    public T GetData<T>() where T : class
    {
        if (string.IsNullOrEmpty(Job?.JobData))
            return default;

        try
        {
            return JsonSerializer.Deserialize<T>(Job.JobData, ConstantJsonOptions.PropNameCaseInsensitive);
        }
        catch (JsonException ex)
        {
            Logger?.Warning(ex, "Failed to deserialize job data to {Type}: {Message}", typeof(T).Name, ex.Message);
            return default;
        }
    }

    /// <inheritdoc/>
    public void Log(string message) => Log(LogLevel.Information, message);

    /// <inheritdoc/>
    public void Log(LogLevel level, string message, Dictionary<string, object> data = null, string category = "UserCode")
    {
        lock (_logLock)
        {
            var log = new OccurrenceLog
            {
                Timestamp = DateTime.UtcNow,
                Level = level.ToString(),
                Message = message,
                Category = category,
                Data = data
            };

            _logs.Add(log);

            if (ExecutorJobConsumerConfig.LogUserFriendlyLogsViaLogger)
                if (Logger != null)
                {
                    switch (level)
                    {
                        case LogLevel.Error:
                            Logger.Error("[{Category}] {Message}", category, message);
                            break;
                        case LogLevel.Warning:
                            Logger.Warning("[{Category}] {Message}", category, message);
                            break;
                        case LogLevel.Information:
                            Logger.Information("[{Category}] {Message}", category, message);
                            break;
                        case LogLevel.Debug:
                            Logger.Debug("[{Category}] {Message}", category, message);
                            break;
                        default:
                            Logger.Information("[{Category}] {Message}", category, message);
                            break;
                    }
                }
                else
                {
                    Console.WriteLine($"[{level}] [{category}] {message}");
                }

            // Publish via OutboxService (local-first, resilient)
            _ = PublishLogViaOutboxAsync(log);
        }
    }

    /// <inheritdoc/>
    public void LogInformation(string message, Dictionary<string, object> data = null) => Log(LogLevel.Information, message, data);

    /// <inheritdoc/>
    public void LogWarning(string message, Dictionary<string, object> data = null) => Log(LogLevel.Warning, message, data);

    /// <inheritdoc/>
    public void LogError(string message, Exception ex = null, Dictionary<string, object> data = null)
    {
        var errorData = data ?? [];

        if (ex != null)
        {
            errorData["ExceptionType"] = ex.GetType().Name;
            errorData["StackTrace"] = ex.StackTrace;
            errorData["InnerException"] = ex.InnerException?.Message;
        }

        var log = new OccurrenceLog
        {
            Timestamp = DateTime.UtcNow,
            Level = "Error",
            Message = message,
            Category = "UserDefined",
            ExceptionType = ex?.GetType().Name,
            Data = errorData
        };

        lock (_logLock)
        {
            _logs.Add(log);

            _ = PublishLogViaOutboxAsync(log);
        }

        if (ExecutorJobConsumerConfig.LogUserFriendlyLogsViaLogger)
            Logger.Error(ex, "[UserDefined] {Message}", message);
    }

    /// <summary>
    /// Publishes log via OutboxService (stores locally first, then syncs).
    /// </summary>
    private async Task PublishLogViaOutboxAsync(OccurrenceLog log)
    {
        try
        {
            if (_outboxService == null)
            {
                Logger?.Warning("OutboxService is NULL! Logs will not be persisted.");
                return;
            }

            await _outboxService.PublishLogAsync(CorrelationId, WorkerId, log, CancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            Console.WriteLine($"[ERROR] Log publishing was canceled: {ex.Message}");
        }
        catch (Exception ex)
        {
            try
            {
                Logger?.Warning(ex, "Failed to publish log via outbox (non-critical): {Message}", ex.Message);
            }
            catch
            {
                Console.WriteLine($"[ERROR] Failed to publish log via outbox: {ex.Message}");
            }
        }
    }

    /// <inheritdoc/>
    public List<OccurrenceLog> GetLogs()
    {
        lock (_logLock)
        {
            return [.. _logs];
        }
    }
}
