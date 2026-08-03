using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Domain.JsonModels;
using Milvasoft.Milvaion.Sdk.Utils;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;

namespace Milvasoft.Milvaion.Sdk.Worker.Quartz.Services;

/// <summary>
/// Interface for publishing external job messages to Milvaion.
/// </summary>
public interface IExternalJobPublisher : IAsyncDisposable
{
    /// <summary>
    /// Publishes a job registration message for upsert in Milvaion.
    /// </summary>
    Task PublishJobRegistrationAsync(ExternalJobRegistrationMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes a job occurrence lifecycle event.
    /// </summary>
    Task PublishOccurrenceEventAsync(ExternalJobOccurrenceMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Publishes external job messages to Milvaion via RabbitMQ.
/// </summary>
public class ExternalJobPublisher(IOptions<WorkerOptions> workerOptions, ILoggerFactory loggerFactory) : IExternalJobPublisher
{
    private readonly WorkerOptions _workerOptions = workerOptions.Value;
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<ExternalJobPublisher>();
    private IConnection _connection;
    private IChannel _channel;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private bool _disposed;

    public async Task PublishJobRegistrationAsync(ExternalJobRegistrationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            await EnsureConnectionAsync(cancellationToken);

            var json = JsonSerializer.Serialize(message);
            var body = Encoding.UTF8.GetBytes(json);

            await _channel!.BasicPublishAsync(exchange: string.Empty,
                                              routingKey: WorkerConstant.Queues.ExternalJobRegistration,
                                              mandatory: false,
                                              body: body,
                                              cancellationToken: cancellationToken);

            _logger?.Debug("Published job registration for {ExternalJobId}", message.ExternalJobId);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Failed to publish job registration for {ExternalJobId}", message.ExternalJobId);
        }
    }

    public async Task PublishOccurrenceEventAsync(ExternalJobOccurrenceMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            await EnsureConnectionAsync(cancellationToken);

            var json = JsonSerializer.Serialize(message);
            var body = Encoding.UTF8.GetBytes(json);

            await _channel!.BasicPublishAsync(exchange: string.Empty,
                                              routingKey: WorkerConstant.Queues.ExternalJobOccurrence,
                                              mandatory: false,
                                              body: body,
                                              cancellationToken: cancellationToken);

            _logger?.Debug("Published occurrence event {EventType} for {ExternalJobId}, CorrelationId: {CorrelationId}", message.EventType, message.ExternalJobId, message.CorrelationId);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Failed to publish occurrence event for {ExternalJobId}, CorrelationId: {CorrelationId}", message.ExternalJobId, message.CorrelationId);
        }
    }

    private async Task EnsureConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection?.IsOpen == true && _channel?.IsOpen == true)
            return;

        await _connectionLock.WaitAsync(cancellationToken);

        try
        {
            if (_connection?.IsOpen == true && _channel?.IsOpen == true)
                return;

            var factory = new ConnectionFactory
            {
                HostName = _workerOptions.RabbitMQ.Host,
                Port = _workerOptions.RabbitMQ.Port,
                UserName = _workerOptions.RabbitMQ.Username,
                Password = _workerOptions.RabbitMQ.Password,
                VirtualHost = _workerOptions.RabbitMQ.VirtualHost
            };

            _connection = await factory.CreateConnectionAsync(cancellationToken);

            // Publisher confirms enabled: BasicPublishAsync below awaits the broker's ack, per https://www.rabbitmq.com/docs/publishers#data-safety.
            _channel = await _connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), cancellationToken);

            // Declare queues
            await _channel.QueueDeclareAsync(queue: WorkerConstant.Queues.ExternalJobRegistration,
                                             durable: true,
                                             exclusive: false,
                                             autoDelete: false,
                                             cancellationToken: cancellationToken);

            await _channel.QueueDeclareAsync(queue: WorkerConstant.Queues.ExternalJobOccurrence,
                                             durable: true,
                                             exclusive: false,
                                             autoDelete: false,
                                             cancellationToken: cancellationToken);

            _logger?.Information("Connected to RabbitMQ at {Host}:{Port}", _workerOptions.RabbitMQ.Host, _workerOptions.RabbitMQ.Port);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_channel != null)
        {
            await _channel.CloseAsync();
            await _channel.DisposeAsync();
        }

        if (_connection != null)
        {
            await _connection.CloseAsync();
            await _connection.DisposeAsync();
        }

        _connectionLock.Dispose();

        GC.SuppressFinalize(this);
    }
}
