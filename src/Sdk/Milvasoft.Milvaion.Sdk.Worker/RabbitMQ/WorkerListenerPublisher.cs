using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Models;
using Milvasoft.Milvaion.Sdk.Utils;
using Milvasoft.Milvaion.Sdk.Worker.Core;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using Milvasoft.Milvaion.Sdk.Worker.Utils;
using RabbitMQ.Client;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Milvasoft.Milvaion.Sdk.Worker.RabbitMQ;

/// <summary>
/// Background service that registers all workers with scheduler and sends periodic heartbeats.
/// Handles multiple job consumers from a single service.
/// </summary>
public class WorkerListenerPublisher(IOptions<WorkerOptions> options,
                                     IMilvaLogger logger,
                                     IServiceProvider serviceProvider,
                                     Dictionary<string, JobConsumerConfig> jobConfigs) : BackgroundService
{
    private readonly WorkerOptions _options = options.Value;
    private readonly IMilvaLogger _logger = logger;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly Dictionary<string, JobConsumerConfig> _jobConfigs = jobConfigs;
    private IConnection _connection;
    private IChannel _channel;
    private readonly string _version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
    private readonly string _hostName = Environment.MachineName;
    private readonly string _ipAddress = GetLocalIPAddress();

    // CPU sampling state: remembers the previous CPU time / wall-clock sample so each heartbeat
    // can report the process CPU% consumed since the last heartbeat.
    private TimeSpan _lastCpuTime = Process.GetCurrentProcess().TotalProcessorTime;
    private DateTime _lastCpuSampleUtc = DateTime.UtcNow;

    // cgroup v2 CPU sampling state (Linux containers). Tracks the previous cpu.stat usage_usec
    // reading so CPU% can be computed the same way "docker stats" does.
    private long _lastCgroupCpuUsageUsec = -1;
    private long _lastCgroupCpuSampleTicks;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information("WorkerRegistrationPublisher starting for {Count} workers...", _jobConfigs.Count);

        var retryCount = 0;
        const int maxRetries = 10;
        const int retryDelaySeconds = 5;

        while (!stoppingToken.IsCancellationRequested && retryCount < maxRetries)
        {
            try
            {
                // Setup RabbitMQ connection
                var factory = new ConnectionFactory
                {
                    HostName = _options.RabbitMQ.Host,
                    Port = _options.RabbitMQ.Port,
                    UserName = _options.RabbitMQ.Username,
                    Password = _options.RabbitMQ.Password,
                    VirtualHost = _options.RabbitMQ.VirtualHost,
                    AutomaticRecoveryEnabled = true,
                    NetworkRecoveryInterval = TimeSpan.FromSeconds(10)
                };

                _connection = await factory.CreateConnectionAsync(stoppingToken);

                // Publisher confirms enabled: BasicPublishAsync below awaits the broker's ack, per https://www.rabbitmq.com/docs/publishers#data-safety.
                _channel = await _connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), stoppingToken);

                // Subscribe to connection recovery events
                _connection.ConnectionRecoveryErrorAsync += async (sender, args) =>
                {
                    _logger.Warning("RabbitMQ connection recovery error: {Error}", args.Exception?.Message);

                    await Task.CompletedTask;
                };

                _connection.RecoverySucceededAsync += async (sender, args) =>
                {
                    _logger.Information("RabbitMQ connection recovered! Re-registering worker...");

                    try
                    {
                        // IMPORTANT: Don't pass stoppingToken here, use CancellationToken.None because recovery might happen during shutdown
                        await RegisterAllWorkersAsync(CancellationToken.None);

                        _logger.Information("Worker re-registered successfully after connection recovery");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Failed to re-register worker after connection recovery");
                    }
                };

                // Declare queues
                await _channel.QueueDeclareAsync(WorkerConstant.Queues.WorkerRegistration, true, false, false, _options.RabbitMQ.BuildQueueArguments(), cancellationToken: stoppingToken);
                await _channel.QueueDeclareAsync(WorkerConstant.Queues.WorkerHeartbeat, true, false, false, _options.RabbitMQ.BuildQueueArguments(), cancellationToken: stoppingToken);

                // Register all workers on startup
                await RegisterAllWorkersAsync(stoppingToken);

                // Reset retry counter on successful connection
                retryCount = 0;

                // Start heartbeat loop
                var heartbeatInterval = _options.Heartbeat?.IntervalSeconds ?? 30;

                _logger.Information("Starting heartbeat loop (interval: {Interval}s)", heartbeatInterval);

                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(heartbeatInterval), stoppingToken);

                    try
                    {
                        await SendAllHeartbeatsAsync(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Heartbeat failed, will retry");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Information("WorkerRegistrationPublisher shutting down");
                break;
            }
            catch (Exception ex)
            {
                retryCount++;

                _logger.Error(ex, "Error in WorkerRegistrationPublisher (attempt {Retry}/{MaxRetries})", retryCount, maxRetries);

                if (retryCount >= maxRetries)
                {
                    _logger.Fatal("WorkerRegistrationPublisher failed after {MaxRetries} attempts. Service will stop.", maxRetries);
                    throw;
                }

                _logger.Information("Retrying connection in {Delay} seconds...", retryDelaySeconds * retryCount);

                await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds * retryCount), stoppingToken);
            }
        }
    }

    /// <summary>
    /// Registers all workers with the scheduler.
    /// </summary>
    private async Task RegisterAllWorkersAsync(CancellationToken cancellationToken = default)
    {
        // Collect all job types for this worker app
        var allJobTypes = _jobConfigs.Keys.ToList();

        _logger.Debug("Registering worker with {Count} job configurations:", _jobConfigs.Count);

        // Register single worker with ALL job types
        var registration = new WorkerDiscoveryRequest
        {
            WorkerId = _options.WorkerId,
            InstanceId = _options.InstanceId,
            DisplayName = $"{_options.WorkerId} ({_options.InstanceId})",
            HostName = _hostName,
            IpAddress = _ipAddress,
            RoutingPatterns = _jobConfigs.ToDictionary(c => c.Key, c => c.Value.RoutingPattern),
            JobDataDefinitions = GetJobDataDefinitions(),
            JobResultDefinitions = GetJobResultDefinitions(),
            JobTypes = allJobTypes,
            MaxParallelJobs = _options.MaxParallelJobs,
            Version = _version,
            Metadata = JsonSerializer.Serialize(new WorkerMetadata
            {
                IsExternal = !string.IsNullOrWhiteSpace(_options.ExternalScheduler.Source),
                ExternalScheduler = _options.ExternalScheduler.Source,
                ProcessorCount = Environment.ProcessorCount,
                OSVersion = Environment.OSVersion.ToString(),
                RuntimeVersion = Environment.Version.ToString(),
                HeartbeatInterval = _options.Heartbeat?.IntervalSeconds ?? 30,
                JobConfigs = _jobConfigs?.Select(kv => new JobConfigMetadata
                {
                    JobType = kv.Key,
                    ConsumerId = kv.Value.ConsumerId,
                    MaxParallelJobs = kv.Value.MaxParallelJobs,
                    ExecutionTimeoutSeconds = kv.Value.ExecutionTimeoutSeconds,
                }).ToList()
            })
        };

        var json = JsonSerializer.Serialize(registration);
        var body = Encoding.UTF8.GetBytes(json);

        await _channel.BasicPublishAsync(exchange: string.Empty,
                                         routingKey: WorkerConstant.Queues.WorkerRegistration,
                                         body: body,
                                         cancellationToken: cancellationToken);

        _logger.Debug("Worker {WorkerId} (Instance: {InstanceId}) registered with {Count} job types: {JobTypes}", _options.WorkerId, _options.InstanceId, allJobTypes.Count, string.Join(", ", allJobTypes));

        _logger.Debug("Routing Patterns: {Patterns}", string.Join(", ", _jobConfigs.Values.Select(c => c.RoutingPattern).Distinct().ToList()));
    }

    /// <summary>
    /// Sends heartbeats for all workers.
    /// </summary>
    internal async Task SendAllHeartbeatsAsync(CancellationToken cancellationToken = default)
    {
        // Get job tracker from DI
        await using var scope = _serviceProvider.CreateAsyncScope();

        var jobTracker = scope.ServiceProvider.GetRequiredService<WorkerJobTracker>();

        // Calculate total current jobs for THIS instance (across all consumers)
        var totalCurrentJobs = jobTracker.GetJobCount(_options.InstanceId);

        // Debug: Log all tracked job counts
        var allCounts = jobTracker.GetAllJobCounts();

        _logger.Debug("JobTracker state: {TrackedInstances} tracked instances. Counts: {Counts}", allCounts.Count, string.Join(", ", allCounts.Select(kvp => $"{kvp.Key}={kvp.Value}")));

        var heartbeat = new WorkerHeartbeatMessage
        {
            WorkerId = _options.WorkerId,       // Worker group ID
            InstanceId = _options.InstanceId,   // Unique instance ID
            CurrentJobs = totalCurrentJobs,     // Jobs on THIS instance
            MemoryBytes = GetProcessMemoryBytes(),
            CpuUsagePercent = GetProcessCpuUsagePercent(),
            Timestamp = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(heartbeat);
        var body = Encoding.UTF8.GetBytes(json);

        await _channel.BasicPublishAsync(exchange: string.Empty,
                                         routingKey: WorkerConstant.Queues.WorkerHeartbeat,
                                         body: body,
                                         cancellationToken: cancellationToken);

        _logger.Debug("Heartbeat sent for worker {WorkerId} instance {InstanceId}: {CurrentJobs} jobs (Tracked in memory: {TrackedJobs})", _options.WorkerId, _options.InstanceId, totalCurrentJobs, allCounts.Count);
    }

    /// <summary>
    /// Gets the current process physical memory usage in bytes.
    /// On Linux containers this reads the cgroup memory accounting (same value "docker stats"
    /// reports: current usage minus reclaimable file cache), falling back to the process
    /// working set when cgroup information is unavailable.
    /// </summary>
    private static long GetProcessMemoryBytes()
    {
        var cgroupMemory = TryGetCgroupMemoryBytes();

        if (cgroupMemory > 0)
            return cgroupMemory;

        using var process = Process.GetCurrentProcess();

        return process.WorkingSet64;
    }

    /// <summary>
    /// Reads container memory usage from the cgroup filesystem (v2 first, then v1).
    /// Returns usage minus inactive file cache to match "docker stats", or 0 when unavailable.
    /// </summary>
    private static long TryGetCgroupMemoryBytes()
    {
        try
        {
            // cgroup v2
            const string v2Current = "/sys/fs/cgroup/memory.current";

            if (File.Exists(v2Current) && long.TryParse(File.ReadAllText(v2Current).Trim(), out var currentV2))
            {
                var inactiveFile = ReadCgroupStatValue("/sys/fs/cgroup/memory.stat", "inactive_file");

                return Math.Max(0, currentV2 - inactiveFile);
            }

            // cgroup v1
            const string v1Usage = "/sys/fs/cgroup/memory/memory.usage_in_bytes";

            if (File.Exists(v1Usage) && long.TryParse(File.ReadAllText(v1Usage).Trim(), out var usageV1))
            {
                var totalInactiveFile = ReadCgroupStatValue("/sys/fs/cgroup/memory/memory.stat", "total_inactive_file");

                return Math.Max(0, usageV1 - totalInactiveFile);
            }
        }
        catch
        {
            // Fall through to working set.
        }

        return 0;
    }

    /// <summary>
    /// Reads a single named value from a cgroup stat file (e.g. memory.stat / cpu.stat).
    /// </summary>
    private static long ReadCgroupStatValue(string path, string key)
    {
        if (!File.Exists(path))
            return 0;

        foreach (var line in File.ReadLines(path))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2 && parts[0] == key && long.TryParse(parts[1], out var value))
                return value;
        }

        return 0;
    }

    /// <summary>
    /// Gets the process CPU usage percentage consumed since the previous heartbeat sample.
    /// On Linux containers this reads cgroup cpu.stat and reports the value the same way
    /// "docker stats" does (summed across cores, i.e. one fully-busy core = 100%), falling
    /// back to <see cref="Process.TotalProcessorTime"/> normalized across cores otherwise.
    /// </summary>
    private double GetProcessCpuUsagePercent()
    {
        var cgroupCpu = TryGetCgroupCpuUsagePercent();

        if (cgroupCpu >= 0)
            return cgroupCpu;

        try
        {
            using var process = Process.GetCurrentProcess();

            var currentCpuTime = process.TotalProcessorTime;
            var nowUtc = DateTime.UtcNow;

            var cpuUsedMs = (currentCpuTime - _lastCpuTime).TotalMilliseconds;
            var elapsedMs = (nowUtc - _lastCpuSampleUtc).TotalMilliseconds;

            _lastCpuTime = currentCpuTime;
            _lastCpuSampleUtc = nowUtc;

            if (elapsedMs <= 0)
                return 0;

            // Summed across cores to match "docker stats" (one fully-busy core = 100%).
            var cpuUsage = cpuUsedMs / elapsedMs * 100.0;

            return Math.Clamp(Math.Round(cpuUsage, 2), 0, 100.0 * Environment.ProcessorCount);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Computes container CPU% from cgroup cpu.stat (usage_usec) delta since the last sample.
    /// Returns -1 when cgroup CPU accounting is unavailable so the caller can fall back.
    /// </summary>
    private double TryGetCgroupCpuUsagePercent()
    {
        try
        {
            const string v2CpuStat = "/sys/fs/cgroup/cpu.stat";

            long usageUsec = -1;

            if (File.Exists(v2CpuStat))
            {
                usageUsec = ReadCgroupStatValue(v2CpuStat, "usage_usec");
            }
            else
            {
                // cgroup v1 reports cpuacct.usage in nanoseconds.
                const string v1CpuAcct = "/sys/fs/cgroup/cpu/cpuacct.usage";
                const string v1CpuAcctAlt = "/sys/fs/cgroup/cpuacct/cpuacct.usage";

                var path = File.Exists(v1CpuAcct) ? v1CpuAcct : File.Exists(v1CpuAcctAlt) ? v1CpuAcctAlt : null;

                if (path != null && long.TryParse(File.ReadAllText(path).Trim(), out var nanos))
                    usageUsec = nanos / 1000;
            }

            if (usageUsec < 0)
                return -1;

            var nowTicks = DateTime.UtcNow.Ticks;

            if (_lastCgroupCpuUsageUsec < 0)
            {
                _lastCgroupCpuUsageUsec = usageUsec;
                _lastCgroupCpuSampleTicks = nowTicks;

                return 0;
            }

            var cpuDeltaUsec = usageUsec - _lastCgroupCpuUsageUsec;
            var elapsedUsec = (nowTicks - _lastCgroupCpuSampleTicks) / 10.0; // 1 tick = 100ns => /10 = microseconds

            _lastCgroupCpuUsageUsec = usageUsec;
            _lastCgroupCpuSampleTicks = nowTicks;

            if (elapsedUsec <= 0)
                return 0;

            // Summed across cores to match "docker stats" (one fully-busy core = 100%).
            var cpuUsage = cpuDeltaUsec / elapsedUsec * 100.0;

            return Math.Clamp(Math.Round(cpuUsage, 2), 0, 100.0 * Environment.ProcessorCount);
        }
        catch
        {
            return -1;
        }
    }

    private static string GetLocalIPAddress()
    {
        try
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());

            foreach (var ip in host.AddressList)
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    return ip.ToString();

            return "Unknown";
        }
        catch
        {
            return "Unknown";
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.Information("WorkerRegistrationPublisher stopping...");

        if (_channel != null)
        {
            await _channel.CloseAsync(cancellationToken);
            _channel.Dispose();
        }

        if (_connection != null)
        {
            await _connection.CloseAsync(cancellationToken);
            _connection.Dispose();
        }

        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Extracts job data definitions from registered job types using reflection.
    /// Uses the generic interface (IAsyncJob&lt;TJobData&gt;) to discover job data types.
    /// </summary>
    private Dictionary<string, string> GetJobDataDefinitions()
    {
        var result = new Dictionary<string, string>();

        foreach (var config in _jobConfigs)
        {
            var jobTypeName = config.Key;
            var jobType = config.Value.JobType;

            if (jobType == null)
                continue;

            var jobDataInfo = JobDataTypeHelper.GetJobDataInfo(jobType);

            if (jobDataInfo?.SchemaJson != null)
            {
                result[jobTypeName] = jobDataInfo.SchemaJson;
                _logger.Debug("Discovered JobData schema for {JobType}: {TypeName}", jobTypeName, jobDataInfo.TypeShortName);
            }
        }

        return result;
    }

    /// <summary>
    /// Extracts job result definitions from registered job types using reflection.
    /// Uses result-producing interfaces (IJobWithResult, IAsyncJobWithResult) to discover result types.
    /// </summary>
    private Dictionary<string, string> GetJobResultDefinitions()
    {
        var result = new Dictionary<string, string>();

        foreach (var config in _jobConfigs)
        {
            var jobTypeName = config.Key;
            var jobType = config.Value.JobType;

            if (jobType == null)
                continue;

            var resultType = JobDataTypeHelper.GetJobResultType(jobType);

            if (resultType == null)
                continue;

            var schemaJson = JobDataTypeHelper.GenerateSchemaJson(resultType);

            if (schemaJson != null)
            {
                result[jobTypeName] = schemaJson;
                _logger.Debug("Discovered JobResult schema for {JobType}: {TypeName}", jobTypeName, resultType.Name);
            }
        }

        return result;
    }
}
