using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Milvaion.Sdk.Domain.JsonModels;
using Milvasoft.Milvaion.Sdk.Utils;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using Milvasoft.Milvaion.Sdk.Worker.RabbitMQ;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.WorkerSdk;

/// <summary>
/// Integration tests for LogPublisher.
/// Tests log message publishing to RabbitMQ.
/// </summary>
[Collection(nameof(MilvaionTestCollection))]
public class LogPublisherTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task PublishLogAsync_ShouldPublishInformationLog()
    {
        // Arrange
        await InitializeAsync();
        await PurgeLogsQueueAsync();

        var correlationId = Guid.CreateVersion7();
        var workerId = $"test-worker-{Guid.NewGuid():N}";
        var uniqueMessage = $"Processing started for batch {Guid.NewGuid():N}";
        var log = new OccurrenceLog
        {
            Timestamp = DateTime.UtcNow,
            Level = "Information",
            Message = uniqueMessage,
            Category = "BatchProcessor"
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        WorkerLogMessage receivedMessage = null;
        (IChannel channel, IConnection connection) = await SetupLogConsumerAsync(msg =>
        {
            if (msg?.CorrelationId == correlationId)
                receivedMessage = msg;
        }, cts.Token);

        await using var publisher = CreateLogPublisher();

        // Act
        await publisher.PublishLogAsync(
            correlationId: correlationId,
            workerId: workerId,
            log: log,
            cancellationToken: cts.Token);

        // Wait for message
        var found = await WaitForConditionAsync(
            () => Task.FromResult(receivedMessage != null),
            timeout: TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromMilliseconds(300),
            cancellationToken: cts.Token);

        // Assert
        found.Should().BeTrue("log message should be received");
        receivedMessage.Should().NotBeNull();
        receivedMessage!.CorrelationId.Should().Be(correlationId);
        receivedMessage.WorkerId.Should().Be(workerId);
        receivedMessage.Log.Should().NotBeNull();
        receivedMessage.Log.Level.Should().Be("Information");
        receivedMessage.Log.Message.Should().Be(uniqueMessage);
        receivedMessage.Log.Category.Should().Be("BatchProcessor");

        await channel.CloseAsync();
        await connection.CloseAsync();
    }

    [Fact]
    public async Task PublishLogAsync_ShouldPublishErrorLog()
    {
        // Arrange
        await InitializeAsync();
        await PurgeLogsQueueAsync();

        var correlationId = Guid.CreateVersion7();
        var workerId = $"test-worker-{Guid.NewGuid():N}";
        var log = new OccurrenceLog
        {
            Timestamp = DateTime.UtcNow,
            Level = "Error",
            Message = $"Failed to connect to database - {Guid.NewGuid():N}",
            Category = "DatabaseConnection",
            Data = new Dictionary<string, object>
            {
                ["ConnectionString"] = "Server=localhost;Database=test",
                ["RetryCount"] = 3
            }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        WorkerLogMessage receivedMessage = null;
        (IChannel channel, IConnection connection) = await SetupLogConsumerAsync(msg =>
        {
            if (msg?.CorrelationId == correlationId)
                receivedMessage = msg;
        }, cts.Token);

        await using var publisher = CreateLogPublisher();

        // Act
        await publisher.PublishLogAsync(
            correlationId: correlationId,
            workerId: workerId,
            log: log,
            cancellationToken: cts.Token);

        // Wait for message
        var found = await WaitForConditionAsync(
            () => Task.FromResult(receivedMessage != null),
            timeout: TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromMilliseconds(300),
            cancellationToken: cts.Token);

        // Assert
        found.Should().BeTrue("error log message should be received");
        receivedMessage.Should().NotBeNull();
        receivedMessage!.Log.Level.Should().Be("Error");
        receivedMessage.Log.Data.Should().NotBeNull();
        receivedMessage.Log.Data.Should().ContainKey("RetryCount");

        await channel.CloseAsync();
        await connection.CloseAsync();
    }

    [Fact]
    public async Task PublishLogAsync_ShouldPublishWarningLog()
    {
        // Arrange
        await InitializeAsync();
        await PurgeLogsQueueAsync();

        var correlationId = Guid.CreateVersion7();
        var workerId = $"test-worker-{Guid.NewGuid():N}";
        var log = new OccurrenceLog
        {
            Timestamp = DateTime.UtcNow,
            Level = "Warning",
            Message = $"Rate limit approaching: 80% of quota used - {Guid.NewGuid():N}",
            Category = "RateLimiter"
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        WorkerLogMessage receivedMessage = null;
        (IChannel channel, IConnection connection) = await SetupLogConsumerAsync(msg =>
        {
            if (msg?.CorrelationId == correlationId)
                receivedMessage = msg;
        }, cts.Token);

        await using var publisher = CreateLogPublisher();

        // Act
        await publisher.PublishLogAsync(
            correlationId: correlationId,
            workerId: workerId,
            log: log,
            cancellationToken: cts.Token);

        // Wait for message
        var found = await WaitForConditionAsync(
            () => Task.FromResult(receivedMessage != null),
            timeout: TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromMilliseconds(300),
            cancellationToken: cts.Token);

        // Assert
        found.Should().BeTrue("warning log message should be received");
        receivedMessage.Should().NotBeNull();
        receivedMessage!.Log.Level.Should().Be("Warning");
        receivedMessage.Log.Message.Should().Contain("Rate limit");

        await channel.CloseAsync();
        await connection.CloseAsync();
    }

    [Fact]
    public async Task PublishLogAsync_ShouldPublishDebugLog()
    {
        // Arrange
        await InitializeAsync();
        await PurgeLogsQueueAsync();

        var correlationId = Guid.CreateVersion7();
        var workerId = $"test-worker-{Guid.NewGuid():N}";
        var log = new OccurrenceLog
        {
            Timestamp = DateTime.UtcNow,
            Level = "Debug",
            Message = $"Entering ProcessItem method - {Guid.NewGuid():N}",
            Category = "ItemProcessor"
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        WorkerLogMessage receivedMessage = null;
        (IChannel channel, IConnection connection) = await SetupLogConsumerAsync(msg =>
        {
            if (msg?.CorrelationId == correlationId)
                receivedMessage = msg;
        }, cts.Token);

        await using var publisher = CreateLogPublisher();

        // Act
        await publisher.PublishLogAsync(
            correlationId: correlationId,
            workerId: workerId,
            log: log,
            cancellationToken: cts.Token);

        // Wait for message
        var found = await WaitForConditionAsync(
            () => Task.FromResult(receivedMessage != null),
            timeout: TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromMilliseconds(300),
            cancellationToken: cts.Token);

        // Assert
        found.Should().BeTrue("debug log message should be received");
        receivedMessage.Should().NotBeNull();
        receivedMessage!.Log.Level.Should().Be("Debug");

        await channel.CloseAsync();
        await connection.CloseAsync();
    }

    [Fact]
    public async Task PublishLogAsync_ShouldIncludeLogData()
    {
        // Arrange
        await InitializeAsync();
        await PurgeLogsQueueAsync();

        var correlationId = Guid.CreateVersion7();
        var workerId = $"test-worker-{Guid.NewGuid():N}";
        var logData = new Dictionary<string, object>
        {
            ["RecordsProcessed"] = 150,
            ["Duration"] = "5.2s",
            ["BatchId"] = $"batch-{Guid.NewGuid():N}",
            ["Success"] = true
        };

        var log = new OccurrenceLog
        {
            Timestamp = DateTime.UtcNow,
            Level = "Information",
            Message = "Batch processing completed",
            Category = "BatchProcessor",
            Data = logData
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        WorkerLogMessage receivedMessage = null;
        (IChannel channel, IConnection connection) = await SetupLogConsumerAsync(msg =>
        {
            if (msg?.CorrelationId == correlationId)
                receivedMessage = msg;
        }, cts.Token);

        await using var publisher = CreateLogPublisher();

        // Act
        await publisher.PublishLogAsync(
            correlationId: correlationId,
            workerId: workerId,
            log: log,
            cancellationToken: cts.Token);

        // Wait for message
        var found = await WaitForConditionAsync(
            () => Task.FromResult(receivedMessage != null),
            timeout: TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromMilliseconds(300),
            cancellationToken: cts.Token);

        // Assert
        found.Should().BeTrue("log message with data should be received");
        receivedMessage.Should().NotBeNull();
        receivedMessage!.Log.Data.Should().NotBeNull();
        receivedMessage.Log.Data.Should().ContainKey("RecordsProcessed");
        receivedMessage.Log.Data.Should().ContainKey("BatchId");

        await channel.CloseAsync();
        await connection.CloseAsync();
    }

    [Fact]
    public async Task PublishLogAsync_ShouldIncludeMessageTimestamp()
    {
        // Arrange
        await InitializeAsync();
        await PurgeLogsQueueAsync();

        var correlationId = Guid.CreateVersion7();
        var beforePublish = DateTime.UtcNow;

        var log = new OccurrenceLog
        {
            Timestamp = DateTime.UtcNow,
            Level = "Information",
            Message = $"Test message - {Guid.NewGuid():N}",
            Category = "Test"
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        WorkerLogMessage receivedMessage = null;
        (IChannel channel, IConnection connection) = await SetupLogConsumerAsync(msg =>
        {
            if (msg?.CorrelationId == correlationId)
                receivedMessage = msg;
        }, cts.Token);

        await using var publisher = CreateLogPublisher();

        // Act
        await publisher.PublishLogAsync(
            correlationId: correlationId,
            workerId: "test-worker",
            log: log,
            cancellationToken: cts.Token);

        // Wait for message
        var found = await WaitForConditionAsync(
            () => Task.FromResult(receivedMessage != null),
            timeout: TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromMilliseconds(300),
            cancellationToken: cts.Token);

        // Assert
        found.Should().BeTrue("log message should be received");
        receivedMessage.Should().NotBeNull();
        receivedMessage!.MessageTimestamp.Should().BeAfter(beforePublish.AddSeconds(-1));
        receivedMessage.MessageTimestamp.Should().BeBefore(DateTime.UtcNow.AddSeconds(1));

        await channel.CloseAsync();
        await connection.CloseAsync();
    }

    [Fact]
    public async Task PublishLogAsync_ShouldPublishMultipleLogsSequentially()
    {
        // Arrange
        await InitializeAsync();
        await PurgeLogsQueueAsync();

        var correlationId = Guid.CreateVersion7();
        var workerId = $"test-worker-{Guid.NewGuid():N}";
        var uniqueCategory = $"SequentialTest_{Guid.NewGuid():N}";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var receivedMessages = new List<WorkerLogMessage>();
        (IChannel channel, IConnection connection) = await SetupMultipleLogConsumerAsync(msg =>
        {
            if (msg?.CorrelationId == correlationId)
                receivedMessages.Add(msg);
        }, cts.Token);

        await using var publisher = CreateLogPublisher();

        // Act
        for (int i = 0; i < 3; i++)
        {
            var log = new OccurrenceLog
            {
                Timestamp = DateTime.UtcNow,
                Level = "Information",
                Message = $"Log message {i}",
                Category = uniqueCategory
            };

            await publisher.PublishLogAsync(
                correlationId: correlationId,
                workerId: workerId,
                log: log,
                cancellationToken: cts.Token);
        }

        // Wait for all 3 messages
        var found = await WaitForConditionAsync(
            () => Task.FromResult(receivedMessages.Count >= 3),
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(300),
            cancellationToken: cts.Token);

        // Assert
        found.Should().BeTrue("all 3 log messages should be received");
        receivedMessages.Should().HaveCount(3);
        receivedMessages.Select(m => m.Log.Message).Should().Contain("Log message 0");
        receivedMessages.Select(m => m.Log.Message).Should().Contain("Log message 1");
        receivedMessages.Select(m => m.Log.Message).Should().Contain("Log message 2");

        await channel.CloseAsync();
        await connection.CloseAsync();
    }

    private async Task PurgeLogsQueueAsync()
    {
        try
        {
            var factory = new ConnectionFactory
            {
                HostName = _factory.GetRabbitMqHost(),
                Port = _factory.GetRabbitMqPort(),
                UserName = "guest",
                Password = "guest"
            };

            await using var connection = await factory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();

            await channel.QueueDeclareAsync(
                queue: WorkerConstant.Queues.WorkerLogs,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null);

            await channel.QueuePurgeAsync(WorkerConstant.Queues.WorkerLogs);
        }
        catch
        {
            // Ignore purge errors
        }
    }

    private LogPublisher CreateLogPublisher()
    {
        var options = new WorkerOptions
        {
            WorkerId = "test-worker",
            RabbitMQ = new RabbitMQSettings
            {
                Host = _factory.GetRabbitMqHost(),
                Port = _factory.GetRabbitMqPort(),
                Username = "guest",
                Password = "guest",
                VirtualHost = "/"
            }
        };

        return new LogPublisher(options, _serviceProvider.GetRequiredService<ILoggerFactory>());
    }

    private async Task<(IChannel, IConnection)> SetupLogConsumerAsync(Action<WorkerLogMessage> onMessage, CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = _factory.GetRabbitMqHost(),
            Port = _factory.GetRabbitMqPort(),
            UserName = "guest",
            Password = "guest"
        };

        // Don't use 'using' - connection needs to stay alive for consumer to work
        var connection = await factory.CreateConnectionAsync(cancellationToken);
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            queue: WorkerConstant.Queues.WorkerLogs,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (model, ea) =>
        {
            try
            {
                var body = ea.Body.ToArray();
                var json = Encoding.UTF8.GetString(body);
                var message = JsonSerializer.Deserialize<WorkerLogMessage>(json, _jsonOptions);

                onMessage(message);

                await channel.BasicAckAsync(ea.DeliveryTag, false);
            }
            catch
            {
                // Ignore errors during message processing
            }
        };

        await channel.BasicConsumeAsync(
            queue: WorkerConstant.Queues.WorkerLogs,
            autoAck: false,
            consumer: consumer,
            cancellationToken: cancellationToken);

        // Register cleanup when cancellation is requested
        cancellationToken.Register(() =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await channel.CloseAsync();
                    await connection.CloseAsync();
                }
                catch (Exception)
                {
                    // Ignore cleanup errors
                }
            });
        });

        return (channel, connection);
    }

    private async Task<(IChannel, IConnection)> SetupMultipleLogConsumerAsync(Action<WorkerLogMessage> onMessage, CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = _factory.GetRabbitMqHost(),
            Port = _factory.GetRabbitMqPort(),
            UserName = "guest",
            Password = "guest"
        };

        // Don't use 'using' - connection needs to stay alive for consumer to work
        var connection = await factory.CreateConnectionAsync(cancellationToken);
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            queue: WorkerConstant.Queues.WorkerLogs,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (model, ea) =>
        {
            try
            {
                var body = ea.Body.ToArray();
                var json = Encoding.UTF8.GetString(body);
                var message = JsonSerializer.Deserialize<WorkerLogMessage>(json, _jsonOptions);

                onMessage(message);

                await channel.BasicAckAsync(ea.DeliveryTag, false);
            }
            catch
            {
                // Ignore errors during message processing
            }
        };

        await channel.BasicConsumeAsync(
            queue: WorkerConstant.Queues.WorkerLogs,
            autoAck: false,
            consumer: consumer,
            cancellationToken: cancellationToken);

        // Register cleanup when cancellation is requested
        cancellationToken.Register(() =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await channel.CloseAsync();
                    await connection.CloseAsync();
                }
                catch (Exception)
                {
                    // Ignore cleanup errors
                }
            });
        });

        return (channel, connection);
    }

    private static async Task<bool> WaitForConditionAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(100);
        var deadline = DateTime.UtcNow.Add(timeout);

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return true;

            await Task.Delay(interval, cancellationToken);
        }

        return false;
    }
}
