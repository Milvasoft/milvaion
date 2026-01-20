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
                _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);

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
                await _channel.QueueDeclareAsync(WorkerConstant.Queues.WorkerRegistration, true, false, false, null, cancellationToken: stoppingToken);
                await _channel.QueueDeclareAsync(WorkerConstant.Queues.WorkerHeartbeat, true, false, false, null, cancellationToken: stoppingToken);

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

        // Collect all routing patterns (union of all job configs)
        var allRoutingPatterns = _jobConfigs.Values.Select(c => c.RoutingPattern).Distinct().ToList();

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
            JobTypes = allJobTypes,
            MaxParallelJobs = _options.MaxParallelJobs,
            Version = _version,
            Metadata = JsonSerializer.Serialize(new
            {
                Environment.ProcessorCount,
                OSVersion = Environment.OSVersion.ToString(),
                RuntimeVersion = Environment.Version.ToString(),
                JobConfigs = _jobConfigs.Select(kv => new
                {
                    JobType = kv.Key,
                    kv.Value.ConsumerId,
                    kv.Value.MaxParallelJobs,
                    kv.Value.ExecutionTimeoutSeconds
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

        _logger.Debug("Routing Patterns: {Patterns}", string.Join(", ", allRoutingPatterns));
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

            if (jobDataInfo != null)
            {
                result[jobTypeName] = jobDataInfo.SchemaJson;
                _logger.Debug("Discovered JobData schema for {JobType}: {TypeName}", jobTypeName, jobDataInfo.TypeShortName);
            }
        }

        return result;
    }
}
