using Microsoft.Extensions.Configuration;
using Milvaion.Application.Dtos.ConfigurationDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Utils;
using System.Diagnostics;
using System.Reflection;

namespace Milvaion.Application.Features.Configuration.GetSystemConfiguration;

/// <summary>
/// Handles the system configuration query.
/// </summary>
/// <param name="configuration"></param>
/// <param name="milvaionConfig"></param>
public class GetSystemConfigurationQueryHandler(IConfiguration configuration, MilvaionConfig milvaionConfig) : IInterceptable, IQueryHandler<GetSystemConfigurationQuery, SystemConfigurationDto>
{
    private readonly IConfiguration _configuration = configuration;
    private readonly MilvaionConfig _milvaionConfig = milvaionConfig;
    private static readonly DateTime _startupTime = DateTime.UtcNow;

    /// <inheritdoc/>
    public Task<Response<SystemConfigurationDto>> Handle(GetSystemConfigurationQuery request, CancellationToken cancellationToken)
    {
        var config = new SystemConfigurationDto
        {
            Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0",
            Environment = _configuration["ASPNETCORE_ENVIRONMENT"] ?? "Unknown",
            HostName = Environment.MachineName,
            StartupTime = _startupTime,
            Uptime = DateTime.UtcNow - _startupTime,
            SystemResources = GetSystemResources(),
            JobDispatcher = new JobDispatcherConfigDto
            {
                Enabled = _milvaionConfig.JobDispatcher.Enabled,
                PollingIntervalSeconds = _milvaionConfig.JobDispatcher.PollingIntervalSeconds,
                BatchSize = _milvaionConfig.JobDispatcher.BatchSize,
                EnableStartupRecovery = _milvaionConfig.JobDispatcher.EnableStartupRecovery,
                LockTtlSeconds = _milvaionConfig.JobDispatcher.LockTtlSeconds,
            },
            Database = new DatabaseConfigDto
            {
                Provider = "PostgreSQL",
                DatabaseName = ExtractFromConnectionString(_configuration.GetConnectionString("DefaultConnectionString"), "Database") ?? "Unknown",
                Host = ExtractFromConnectionString(_configuration.GetConnectionString("DefaultConnectionString"), "Host") ?? "Unknown"
            },
            Redis = new RedisConfigDto
            {
                ConnectionString = _milvaionConfig.Redis.ConnectionString,
                Database = _milvaionConfig.Redis.Database,
                ConnectTimeout = _milvaionConfig.Redis.ConnectTimeout,
                DefaultLockTtlSeconds = _milvaionConfig.Redis.DefaultLockTtlSeconds,
                KeyPrefix = _milvaionConfig.Redis.KeyPrefix,
                SyncTimeout = _milvaionConfig.Redis.SyncTimeout
            },
            RabbitMQ = new RabbitMQConfigDto
            {
                Host = _milvaionConfig.RabbitMQ.Host,
                Port = _milvaionConfig.RabbitMQ.Port,
                VirtualHost = _milvaionConfig.RabbitMQ.VirtualHost,
                Exchange = WorkerConstant.ExchangeName,
                DeadLetterExchange = WorkerConstant.DeadLetterExchangeName,
                AutoDelete = _milvaionConfig.RabbitMQ.AutoDelete,
                Durable = _milvaionConfig.RabbitMQ.Durable,
                ConnectionTimeout = _milvaionConfig.RabbitMQ.ConnectionTimeout,
                Heartbeat = _milvaionConfig.RabbitMQ.Heartbeat,
                AutomaticRecoveryEnabled = _milvaionConfig.RabbitMQ.AutomaticRecoveryEnabled,
                NetworkRecoveryInterval = _milvaionConfig.RabbitMQ.NetworkRecoveryInterval,
                QueueDepthWarningThreshold = _milvaionConfig.RabbitMQ.QueueDepthWarningThreshold,
                QueueDepthCriticalThreshold = _milvaionConfig.RabbitMQ.QueueDepthCriticalThreshold,
                Queues = new RabbitMQQueuesDto
                {
                    ScheduledJobs = WorkerConstant.Queues.Jobs,
                    WorkerLogs = WorkerConstant.Queues.WorkerLogs,
                    StatusUpdates = WorkerConstant.Queues.StatusUpdates,
                    WorkerHeartbeat = WorkerConstant.Queues.WorkerHeartbeat,
                    WorkerRegistration = WorkerConstant.Queues.WorkerRegistration,
                    FailedOccurrences = WorkerConstant.Queues.FailedOccurrences,
                }
            },
            JobAutoDisable = new JobAutoDisableOptions
            {
                Enabled = _milvaionConfig.JobAutoDisable.Enabled,
                ConsecutiveFailureThreshold = _milvaionConfig.JobAutoDisable.ConsecutiveFailureThreshold,
                FailureWindowMinutes = _milvaionConfig.JobAutoDisable.FailureWindowMinutes,
            },
            ApiKeyVersion = _milvaionConfig.ApiKey?.Version ?? 1,
            BackgroundServices = ReadBackgroundServices(),
            Observability = ReadObservability(),
            Alerting = ReadAlerting()
        };

        return Task.FromResult(Response<SystemConfigurationDto>.Success(config));
    }

    /// <summary>
    /// Reads the background service settings.
    /// </summary>
    /// <remarks>
    /// These sections have no typed counterpart on <c>MilvaionConfig</c>, so they are read
    /// straight from configuration. The indexer is used rather than the binder because
    /// <c>Get&lt;T&gt;</c> lives in a separate package this project does not reference, and
    /// twenty or so values are not worth taking a dependency for.
    ///
    /// A setting that was never written falls back to the service's own default - a missing
    /// section means "not configured", not "switched off".
    /// </remarks>
    private BackgroundServicesConfigDto ReadBackgroundServices()
    {
        var root = _configuration.GetSection("MilvaionConfig");

        var zombie = root.GetSection("ZombieOccurrenceDetector");
        var logCollector = root.GetSection("LogCollector");
        var statusTracker = root.GetSection("StatusTracker");
        var externalTracker = root.GetSection("ExternalJobTracker");
        var workflowEngine = root.GetSection("WorkflowEngine");

        return new BackgroundServicesConfigDto
        {
            WorkerAutoDiscovery = new ToggleConfigDto
            {
                Enabled = Flag(root.GetSection("WorkerAutoDiscovery"), "Enabled", true)
            },
            ZombieOccurrenceDetector = new ZombieDetectorConfigDto
            {
                Enabled = Flag(zombie, "Enabled", true),
                CheckIntervalSeconds = Num(zombie, "CheckIntervalSeconds", 100),
                ZombieTimeoutMinutes = Num(zombie, "ZombieTimeoutMinutes", 5)
            },
            LogCollector = new BatchServiceConfigDto
            {
                Enabled = Flag(logCollector, "Enabled", true),
                BatchSize = Num(logCollector, "BatchSize", 10000),
                BatchIntervalMs = Num(logCollector, "BatchIntervalMs", 500)
            },
            StatusTracker = new StatusTrackerConfigDto
            {
                Enabled = Flag(statusTracker, "Enabled", true),
                BatchSize = Num(statusTracker, "BatchSize", 5000),
                BatchIntervalMs = Num(statusTracker, "BatchIntervalMs", 100),
                ExecutionLogMaxCount = Num(statusTracker, "ExecutionLogMaxCount", 100)
            },
            FailedOccurrenceHandler = new ToggleConfigDto
            {
                Enabled = Flag(root.GetSection("FailedOccurrenceHandler"), "Enabled", true)
            },
            ExternalJobTracker = new ExternalJobTrackerConfigDto
            {
                Enabled = Flag(externalTracker, "Enabled", true),
                RegistrationBatchSize = Num(externalTracker, "RegistrationBatchSize", 50),
                OccurrenceBatchSize = Num(externalTracker, "OccurrenceBatchSize", 100),
                BatchIntervalMs = Num(externalTracker, "BatchIntervalMs", 500)
            },
            WorkflowEngine = new PollingServiceConfigDto
            {
                Enabled = Flag(workflowEngine, "Enabled", true),
                PollingIntervalSeconds = Num(workflowEngine, "PollingIntervalSeconds", 5)
            }
        };
    }

    /// <summary>
    /// Reads the log and metric export settings.
    /// </summary>
    /// <remarks>
    /// The Seq address is exposed because it carries no credentials; Seq's API key is a
    /// separate setting and never passes through here.
    /// </remarks>
    private ObservabilityConfigDto ReadObservability()
    {
        var root = _configuration.GetSection("MilvaionConfig");

        var seq = root.GetSection("Logging:Seq");
        var otel = root.GetSection("OpenTelemetry");

        return new ObservabilityConfigDto
        {
            Seq = new SeqConfigDto
            {
                Enabled = Flag(seq, "Enabled"),
                Uri = seq["Uri"]
            },
            OpenTelemetry = new OpenTelemetryConfigDto
            {
                Enabled = Flag(otel, "Enabled"),
                ExportPath = otel["ExportPath"],
                Service = otel["Service"],
                Environment = otel["Environment"],
                Job = otel["Job"],
                Instance = otel["Instance"]
            }
        };
    }

    /// <summary>
    /// Reads the alerting settings.
    /// </summary>
    /// <remarks>
    /// Per channel this returns only the status and the <em>name</em> of the default target.
    /// Webhook URLs and SMTP credentials are deliberately left out: anyone who can see this
    /// screen would see the address too, and knowing a webhook URL is the same as being able
    /// to post to that channel.
    /// </remarks>
    private AlertingConfigDto ReadAlerting()
    {
        var alerting = _milvaionConfig.Alerting;

        if (alerting is null)
            return new AlertingConfigDto();

        var channels = new List<AlertChannelStatusDto>();

        // The channel level flag may be left unset, in which case the global setting applies.
        void Add(string name, bool enabled, bool? onlyProduction, string target)
            => channels.Add(new AlertChannelStatusDto
            {
                Name = name,
                Enabled = enabled,
                SendOnlyInProduction = onlyProduction ?? alerting.SendOnlyInProduction,
                DefaultTarget = target
            });

        var c = alerting.Channels;

        if (c is not null)
        {
            if (c.GoogleChat is not null)
                Add("Google Chat", c.GoogleChat.Enabled, c.GoogleChat.SendOnlyInProduction, c.GoogleChat.DefaultSpace);

            if (c.Slack is not null)
                Add("Slack", c.Slack.Enabled, c.Slack.SendOnlyInProduction, c.Slack.DefaultChannel);

            if (c.Teams is not null)
                Add("Microsoft Teams", c.Teams.Enabled, c.Teams.SendOnlyInProduction, c.Teams.DefaultChannel);

            if (c.Email is not null)
                Add("Email", c.Email.Enabled, c.Email.SendOnlyInProduction, c.Email.DisplayName);

            if (c.InternalNotification is not null)
                Add("In-app", c.InternalNotification.Enabled, c.InternalNotification.SendOnlyInProduction, "Dashboard");
        }

        return new AlertingConfigDto
        {
            MilvaionAppUrl = alerting.MilvaionAppUrl,
            DefaultChannel = alerting.DefaultChannel,
            SendOnlyInProduction = alerting.SendOnlyInProduction,
            Channels = channels,
            ConfiguredAlertCount = alerting.Alerts?.Count ?? 0,
            EnabledAlertCount = alerting.Alerts?.Count(a => a.Value?.Enabled == true) ?? 0
        };
    }

    /// <summary>
    /// Reads a flag from a section, falling back to the default when it is absent or unparseable.
    /// </summary>
    private static bool Flag(IConfigurationSection section, string key, bool fallback = false)
        => bool.TryParse(section[key], out var value) ? value : fallback;

    /// <summary>
    /// Reads a number from a section, falling back to the default when it is absent or unparseable.
    /// </summary>
    private static int Num(IConfigurationSection section, string key, int fallback)
        => int.TryParse(section[key], out var value) ? value : fallback;

    private static SystemResourcesDto GetSystemResources()
    {
        try
        {
            var currentProcess = Process.GetCurrentProcess();

            // Memory metrics
            var gcMemoryInfo = GC.GetGCMemoryInfo();
            var totalMemoryBytes = gcMemoryInfo.TotalAvailableMemoryBytes;
            var usedMemoryBytes = GC.GetTotalMemory(false);
            var availableMemoryBytes = totalMemoryBytes - usedMemoryBytes;
            var totalMemoryMB = totalMemoryBytes / 1024 / 1024;
            var usedMemoryMB = usedMemoryBytes / 1024 / 1024;
            var availableMemoryMB = availableMemoryBytes / 1024 / 1024;
            var memoryUsagePercent = totalMemoryMB > 0 ? (double)usedMemoryMB / totalMemoryMB * 100 : 0;
            var processMemoryMB = currentProcess.WorkingSet64 / 1024 / 1024;

            // CPU metrics (approximate)
            var cpuUsage = currentProcess.TotalProcessorTime.TotalMilliseconds /
                          (DateTime.UtcNow - currentProcess.StartTime.ToUniversalTime()).TotalMilliseconds /
                          Environment.ProcessorCount * 100;

            // Disk metrics
            var drives = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed);
            var totalDiskGB = drives.Sum(d => d.TotalSize) / 1024 / 1024 / 1024;
            var availableDiskGB = drives.Sum(d => d.AvailableFreeSpace) / 1024 / 1024 / 1024;
            var diskUsagePercent = totalDiskGB > 0 ? (double)(totalDiskGB - availableDiskGB) / totalDiskGB * 100 : 0;

            return new SystemResourcesDto
            {
                CpuUsagePercent = Math.Round(Math.Min(cpuUsage, 100), 2),
                TotalMemoryMB = totalMemoryMB,
                UsedMemoryMB = usedMemoryMB,
                AvailableMemoryMB = availableMemoryMB,
                MemoryUsagePercent = Math.Round(memoryUsagePercent, 2),
                ProcessMemoryMB = processMemoryMB,
                TotalDiskGB = totalDiskGB,
                AvailableDiskGB = availableDiskGB,
                DiskUsagePercent = Math.Round(diskUsagePercent, 2)
            };
        }
        catch
        {
            // Return default values if metrics collection fails
            return new SystemResourcesDto
            {
                CpuUsagePercent = 0,
                TotalMemoryMB = 0,
                UsedMemoryMB = 0,
                AvailableMemoryMB = 0,
                MemoryUsagePercent = 0,
                ProcessMemoryMB = 0,
                TotalDiskGB = 0,
                AvailableDiskGB = 0,
                DiskUsagePercent = 0
            };
        }
    }

    private static string ExtractFromConnectionString(string connectionString, string key)
    {
        if (string.IsNullOrEmpty(connectionString))
            return null;

        var parts = connectionString.Split(';');

        var part = parts.FirstOrDefault(p => p.Trim().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));

        return part?.Split('=').LastOrDefault()?.Trim();
    }
}
