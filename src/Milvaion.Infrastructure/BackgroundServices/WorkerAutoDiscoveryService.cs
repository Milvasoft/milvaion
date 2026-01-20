using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces.Redis;
using Milvaion.Application.Utils.Constants;
using Milvaion.Infrastructure.BackgroundServices.Base;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Models;
using Milvasoft.Milvaion.Sdk.Utils;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text.Json;

namespace Milvaion.Infrastructure.BackgroundServices;

/// <summary>
/// Consumes worker registration and heartbeat messages from RabbitMQ.
/// Stores runtime state in Redis for high performance.
/// </summary>
public class WorkerAutoDiscoveryService(IRedisWorkerService redisWorkerService,
                                        IOptions<RabbitMQOptions> rabbitOptions,
                                        IOptions<WorkerAutoDiscoveryOptions> options,
                                        ILoggerFactory loggerFactory,
                                        IMemoryStatsRegistry memoryStatsRegistry = null) : MemoryTrackedBackgroundService(loggerFactory, options.Value, memoryStatsRegistry)
{
    private readonly IRedisWorkerService _redisWorkerService = redisWorkerService;
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<WorkerAutoDiscoveryService>();
    private readonly RabbitMQOptions _rabbitOptions = rabbitOptions.Value;
    private readonly WorkerAutoDiscoveryOptions _options = options.Value;
    private IConnection _connection;
    private IChannel _registrationChannel;
    private IChannel _heartbeatChannel;

    /// <inheritdoc/>
    protected override string ServiceName => "WorkerAutoDiscovery";

    /// <summary>
    /// Executes the background service to listen for worker messages.
    /// </summary>
    /// <param name="stoppingToken"></param>
    /// <returns></returns>
    protected override async Task ExecuteWithMemoryTrackingAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.Warning("Worker auto discovery is disabled. Skipping startup.");

            return;
        }

        _logger.Information("Worker auto discovery is starting (Redis-based)...");

        try
        {
            await ConnectAndConsumeAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.Information("Worker auto discovery is shutting down");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Fatal error in worker auto discovery");
            throw;
        }
    }

    private async Task ConnectAndConsumeAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = _rabbitOptions.Host,
            Port = _rabbitOptions.Port,
            UserName = _rabbitOptions.Username,
            Password = _rabbitOptions.Password,
            VirtualHost = _rabbitOptions.VirtualHost,
            AutomaticRecoveryEnabled = true
        };

        _connection = await factory.CreateConnectionAsync(stoppingToken);
        _registrationChannel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);
        _heartbeatChannel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);

        // Declare queues
        await _registrationChannel.QueueDeclareAsync(WorkerConstant.Queues.WorkerRegistration, true, false, false, null, cancellationToken: stoppingToken);
        await _heartbeatChannel.QueueDeclareAsync(WorkerConstant.Queues.WorkerHeartbeat, true, false, false, null, cancellationToken: stoppingToken);

        _logger.Information("Connected to RabbitMQ. Consuming worker registration and heartbeat messages");

        // Registration consumer
        var registrationConsumer = new AsyncEventingBasicConsumer(_registrationChannel);

        registrationConsumer.ReceivedAsync += async (model, ea) =>
        {
            await ProcessRegistrationAsync(ea, stoppingToken);

            TrackMemoryAfterIteration();
        };

        await _registrationChannel.BasicConsumeAsync(WorkerConstant.Queues.WorkerRegistration, false, registrationConsumer, stoppingToken);

        // Heartbeat consumer
        var heartbeatConsumer = new AsyncEventingBasicConsumer(_heartbeatChannel);

        heartbeatConsumer.ReceivedAsync += async (model, ea) =>
        {
            await ProcessHeartbeatAsync(ea, stoppingToken);

            TrackMemoryAfterIteration();
        };

        await _heartbeatChannel.BasicConsumeAsync(WorkerConstant.Queues.WorkerHeartbeat, false, heartbeatConsumer, stoppingToken);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task ProcessRegistrationAsync(BasicDeliverEventArgs ea, CancellationToken cancellationToken)
    {
        try
        {
            var registration = JsonSerializer.Deserialize<WorkerDiscoveryRequest>(ea.Body.Span, ConstantJsonOptions.PropNameCaseInsensitive);

            if (registration == null)
            {
                _logger.Debug("Failed to deserialize worker registration");

                await _registrationChannel.BasicNackAsync(ea.DeliveryTag, false, false, cancellationToken);

                return;
            }

            // Store in Redis (fast, in-memory)
            var success = await _redisWorkerService.RegisterWorkerAsync(registration, cancellationToken);

            if (success)
            {
                var existingWorker = await _redisWorkerService.GetWorkerAsync(registration.WorkerId, cancellationToken);
                var instanceCount = existingWorker?.Instances?.Count ?? 1;

                _logger.Information("Worker {WorkerId} (Instance: {InstanceId}) registered in Redis. Total instances: {Count}", registration.WorkerId, registration.InstanceId, instanceCount);
            }
            else
            {
                _logger.Error("Failed to register worker {WorkerId} in Redis", registration.WorkerId);
            }

            await _registrationChannel.BasicAckAsync(ea.DeliveryTag, false, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to process worker registration");

            await _registrationChannel.BasicNackAsync(ea.DeliveryTag, false, false, cancellationToken);
        }
    }

    private async Task ProcessHeartbeatAsync(BasicDeliverEventArgs ea, CancellationToken cancellationToken)
    {
        try
        {
            var heartbeat = JsonSerializer.Deserialize<WorkerHeartbeatMessage>(ea.Body.Span, ConstantJsonOptions.PropNameCaseInsensitive);

            if (heartbeat == null)
            {
                await _heartbeatChannel.BasicNackAsync(ea.DeliveryTag, false, false, cancellationToken);
                return;
            }

            _logger.Debug("Received heartbeat: WorkerId={WorkerId}, InstanceId={InstanceId}, CurrentJobs={CurrentJobs}", heartbeat.WorkerId, heartbeat.InstanceId, heartbeat.CurrentJobs);

            // Update in Redis (fast, in-memory)
            var success = await _redisWorkerService.UpdateHeartbeatAsync(heartbeat.WorkerId, heartbeat.InstanceId, heartbeat.CurrentJobs, cancellationToken);

            if (!success)
            {
                _logger.Warning("Heartbeat for unknown worker {WorkerId} instance {InstanceId}", heartbeat.WorkerId, heartbeat.InstanceId);
            }
            else
            {
                _logger.Debug("Heartbeat processed successfully for {WorkerId}/{InstanceId}", heartbeat.WorkerId, heartbeat.InstanceId);
            }

            await _heartbeatChannel.BasicAckAsync(ea.DeliveryTag, false, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to process worker heartbeat");

            await _heartbeatChannel.BasicNackAsync(ea.DeliveryTag, false, false, cancellationToken);
        }
    }

    /// <summary>
    /// Stops the background service and cleans up resources.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.Information("Worker auto discovery is stopping...");

        if (_registrationChannel != null)
        {
            await _registrationChannel.CloseAsync(cancellationToken);
            _registrationChannel.Dispose();
        }

        if (_heartbeatChannel != null)
        {
            await _heartbeatChannel.CloseAsync(cancellationToken);
            _heartbeatChannel.Dispose();
        }

        if (_connection != null)
        {
            await _connection.CloseAsync(cancellationToken);
            _connection.Dispose();
        }

        await base.StopAsync(cancellationToken);
    }
}
