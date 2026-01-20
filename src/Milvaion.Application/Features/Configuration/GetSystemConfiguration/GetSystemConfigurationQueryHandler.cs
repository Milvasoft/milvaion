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
            }
        };

        return Task.FromResult(Response<SystemConfigurationDto>.Success(config));
    }

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
