using Microsoft.Extensions.Logging;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Utils;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;

namespace Milvasoft.Milvaion.Sdk.Worker.RabbitMQ;

/// <summary>
/// Interface for publishing worker logs.
/// </summary>
public interface ILogPublisher : IAsyncDisposable
{
    /// <summary>
    /// Publishes a log entry to RabbitMQ.
    /// </summary>
    Task PublishLogAsync(Guid correlationId,
                         string workerId,
                         OccurrenceLog log,
                         CancellationToken cancellationToken = default);
}

/// <summary>
/// Publishes worker logs to RabbitMQ for collection by producer.
/// </summary>
public class LogPublisher(WorkerOptions options, ILoggerFactory loggerFactory) : ILogPublisher
{
    private readonly WorkerOptions _options = options;
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<LogPublisher>();
    private IConnection _connection;
    private IChannel _channel;

    public async Task PublishLogAsync(Guid correlationId,
                                      string workerId,
                                      OccurrenceLog log,
                                      CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureConnectionAsync(cancellationToken);

            var message = new WorkerLogMessage
            {
                CorrelationId = correlationId,
                WorkerId = workerId,
                Log = log,
                MessageTimestamp = DateTime.UtcNow
            };

            var json = JsonSerializer.Serialize(message);
            var body = Encoding.UTF8.GetBytes(json);

            await _channel.BasicPublishAsync(exchange: string.Empty,
                                             routingKey: WorkerConstant.Queues.WorkerLogs,
                                             mandatory: false,
                                             body: body,
                                             cancellationToken: cancellationToken);

            _logger.Debug("Published log for CorrelationId: {CorrelationId}", correlationId);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Failed to publish log for CorrelationId: {CorrelationId}", correlationId);
        }
    }

    private async Task EnsureConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection == null || !_connection.IsOpen)
        {
            var factory = new ConnectionFactory
            {
                HostName = _options.RabbitMQ.Host,
                Port = _options.RabbitMQ.Port,
                UserName = _options.RabbitMQ.Username,
                Password = _options.RabbitMQ.Password,
                VirtualHost = _options.RabbitMQ.VirtualHost
            };

            _connection = await factory.CreateConnectionAsync(cancellationToken);
            _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);

            await _channel.QueueDeclareAsync(queue: WorkerConstant.Queues.WorkerLogs,
                                             durable: true,
                                             exclusive: false,
                                             autoDelete: false,
                                             arguments: null,
                                             cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// Dispose failed connection to allow reconnection on next attempt.
    /// </summary>
    private async Task DisposeConnectionAsync()
    {
        try
        {
            if (_channel != null)
            {
                await _channel.CloseAsync();
                _channel.Dispose();
                _channel = null;
            }

            if (_connection != null)
            {
                await _connection.CloseAsync();
                _connection.Dispose();
                _connection = null;
            }
        }
        catch
        {
            // Ignore disposal errors
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeConnectionAsync();
        GC.SuppressFinalize(this);
    }
}