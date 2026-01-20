using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
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
/// Integration tests for StatusUpdatePublisher.
/// Tests status update message publishing to RabbitMQ.
/// </summary>
[Collection(nameof(MilvaionTestCollection))]
public class StatusUpdatePublisherTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task PublishStatusAsync_ShouldPublishRunningStatus()
    {
        // Arrange
        await InitializeAsync();
        await PurgeStatusUpdatesQueueAsync();

        var correlationId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var workerId = $"test-worker-{Guid.NewGuid():N}";
        var startTime = DateTime.UtcNow;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        JobStatusUpdateMessage receivedMessage = null;
        (IChannel channel, IConnection connection) = await SetupStatusUpdateConsumerAsync(msg =>
        {
            if (msg?.CorrelationId == correlationId)
                receivedMessage = msg;
        }, cts.Token);

        await using var publisher = CreateStatusUpdatePublisher();

        // Act
        await publisher.PublishStatusAsync(
            correlationId: correlationId,
            jobId: jobId,
            workerId: workerId,
            status: JobOccurrenceStatus.Running,
            startTime: startTime,
            cancellationToken: cts.Token);

        // Wait for message
        var found = await WaitForConditionAsync(
            () => Task.FromResult(receivedMessage != null),
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(300),
            cancellationToken: cts.Token);

        // Assert
        found.Should().BeTrue("status update message should be received");
        receivedMessage.Should().NotBeNull();
        receivedMessage!.CorrelationId.Should().Be(correlationId);
        receivedMessage.JobId.Should().Be(jobId);
        receivedMessage.WorkerId.Should().Be(workerId);
        receivedMessage.Status.Should().Be(JobOccurrenceStatus.Running);
        receivedMessage.StartTime.Should().NotBeNull();

        await channel.CloseAsync();
        await connection.CloseAsync();
    }

    [Fact]
    public async Task PublishStatusAsync_ShouldPublishCompletedStatusWithResult()
    {
        // Arrange
        await InitializeAsync();
        await PurgeStatusUpdatesQueueAsync();

        var correlationId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var workerId = $"test-worker-{Guid.NewGuid():N}";
        var startTime = DateTime.UtcNow.AddSeconds(-10);
        var endTime = DateTime.UtcNow;
        var durationMs = 10000L;
        var result = $"Job completed successfully - {Guid.NewGuid():N}";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        JobStatusUpdateMessage receivedMessage = null;
        (IChannel channel, IConnection connection) = await SetupStatusUpdateConsumerAsync(msg =>
        {
            if (msg?.CorrelationId == correlationId)
                receivedMessage = msg;
        }, cts.Token);

        await using var publisher = CreateStatusUpdatePublisher();

        // Act
        await publisher.PublishStatusAsync(
            correlationId: correlationId,
            jobId: jobId,
            workerId: workerId,
            status: JobOccurrenceStatus.Completed,
            startTime: startTime,
            endTime: endTime,
            durationMs: durationMs,
            result: result,
            cancellationToken: cts.Token);

        // Wait for message
        var found = await WaitForConditionAsync(
            () => Task.FromResult(receivedMessage != null),
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(300),
            cancellationToken: cts.Token);

        // Assert
        found.Should().BeTrue("completed status message should be received");
        receivedMessage.Should().NotBeNull();
        receivedMessage!.Status.Should().Be(JobOccurrenceStatus.Completed);
        receivedMessage.EndTime.Should().NotBeNull();
        receivedMessage.DurationMs.Should().Be(durationMs);
        receivedMessage.Result.Should().Be(result);

        await channel.CloseAsync();
        await connection.CloseAsync();
    }

    [Fact]
    public async Task PublishStatusAsync_ShouldPublishFailedStatusWithException()
    {
        // Arrange
        await InitializeAsync();
        await PurgeStatusUpdatesQueueAsync();

        var correlationId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var workerId = $"test-worker-{Guid.NewGuid():N}";
        var exception = $"NullReferenceException: {Guid.NewGuid():N}";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        JobStatusUpdateMessage receivedMessage = null;
        (IChannel channel, IConnection connection) = await SetupStatusUpdateConsumerAsync(msg =>
        {
            if (msg?.CorrelationId == correlationId)
                receivedMessage = msg;
        }, cts.Token);

        await using var publisher = CreateStatusUpdatePublisher();

        // Act
        await publisher.PublishStatusAsync(
            correlationId: correlationId,
            jobId: jobId,
            workerId: workerId,
            status: JobOccurrenceStatus.Failed,
            endTime: DateTime.UtcNow,
            exception: exception,
            cancellationToken: cts.Token);

        // Wait for message
        var found = await WaitForConditionAsync(
            () => Task.FromResult(receivedMessage != null),
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(300),
            cancellationToken: cts.Token);

        // Assert
        found.Should().BeTrue("failed status message should be received");
        receivedMessage.Should().NotBeNull();
        receivedMessage!.Status.Should().Be(JobOccurrenceStatus.Failed);
        receivedMessage.Exception.Should().Contain("NullReferenceException");

        await channel.CloseAsync();
        await connection.CloseAsync();
    }

    [Fact]
    public async Task PublishStatusAsync_ShouldPublishCancelledStatus()
    {
        // Arrange
        await InitializeAsync();
        await PurgeStatusUpdatesQueueAsync();

        var correlationId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var workerId = $"test-worker-{Guid.NewGuid():N}";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        JobStatusUpdateMessage receivedMessage = null;
        (IChannel channel, IConnection connection) = await SetupStatusUpdateConsumerAsync(msg =>
        {
            if (msg?.CorrelationId == correlationId)
                receivedMessage = msg;
        }, cts.Token);

        await using var publisher = CreateStatusUpdatePublisher();

        // Act
        await publisher.PublishStatusAsync(
            correlationId: correlationId,
            jobId: jobId,
            workerId: workerId,
            status: JobOccurrenceStatus.Cancelled,
            endTime: DateTime.UtcNow,
            result: "Job cancelled by user request",
            cancellationToken: cts.Token);

        // Wait for message
        var found = await WaitForConditionAsync(
            () => Task.FromResult(receivedMessage != null),
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(300),
            cancellationToken: cts.Token);

        // Assert
        found.Should().BeTrue("cancelled status message should be received");
        receivedMessage.Should().NotBeNull();
        receivedMessage!.Status.Should().Be(JobOccurrenceStatus.Cancelled);
        receivedMessage.Result.Should().Contain("cancelled");

        await channel.CloseAsync();
        await connection.CloseAsync();
    }

    [Fact]
    public async Task PublishStatusAsync_ShouldPublishTimedOutStatus()
    {
        // Arrange
        await InitializeAsync();
        await PurgeStatusUpdatesQueueAsync();

        var correlationId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var workerId = $"test-worker-{Guid.NewGuid():N}";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        JobStatusUpdateMessage receivedMessage = null;
        (IChannel channel, IConnection connection) = await SetupStatusUpdateConsumerAsync(msg =>
        {
            if (msg?.CorrelationId == correlationId)
                receivedMessage = msg;
        }, cts.Token);

        await using var publisher = CreateStatusUpdatePublisher();

        // Act
        await publisher.PublishStatusAsync(
            correlationId: correlationId,
            jobId: jobId,
            workerId: workerId,
            status: JobOccurrenceStatus.TimedOut,
            endTime: DateTime.UtcNow,
            exception: "Job execution timed out after 3600 seconds",
            cancellationToken: cts.Token);

        // Wait for message
        var found = await WaitForConditionAsync(
            () => Task.FromResult(receivedMessage != null),
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(300),
            cancellationToken: cts.Token);

        // Assert
        found.Should().BeTrue("timed out status message should be received");
        receivedMessage.Should().NotBeNull();
        receivedMessage!.Status.Should().Be(JobOccurrenceStatus.TimedOut);
        receivedMessage.Exception.Should().Contain("timed out");

        await channel.CloseAsync();
        await connection.CloseAsync();
    }

    [Fact]
    public async Task PublishStatusAsync_ShouldIncludeMessageTimestamp()
    {
        // Arrange
        await InitializeAsync();
        await PurgeStatusUpdatesQueueAsync();

        var correlationId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var beforePublish = DateTime.UtcNow;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        JobStatusUpdateMessage receivedMessage = null;
        (IChannel channel, IConnection connection) = await SetupStatusUpdateConsumerAsync(msg =>
        {
            if (msg?.CorrelationId == correlationId)
                receivedMessage = msg;
        }, cts.Token);

        await using var publisher = CreateStatusUpdatePublisher();

        // Act
        await publisher.PublishStatusAsync(
            correlationId: correlationId,
            jobId: jobId,
            workerId: "test-worker",
            status: JobOccurrenceStatus.Running,
            cancellationToken: cts.Token);

        // Wait for message
        var found = await WaitForConditionAsync(
            () => Task.FromResult(receivedMessage != null),
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(300),
            cancellationToken: cts.Token);

        // Assert
        found.Should().BeTrue("status message should be received");
        receivedMessage.Should().NotBeNull();
        receivedMessage!.MessageTimestamp.Should().BeAfter(beforePublish.AddSeconds(-1));
        receivedMessage.MessageTimestamp.Should().BeBefore(DateTime.UtcNow.AddSeconds(1));

        await channel.CloseAsync();
        await connection.CloseAsync();
    }

    private async Task PurgeStatusUpdatesQueueAsync()
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
                queue: WorkerConstant.Queues.StatusUpdates,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null);

            await channel.QueuePurgeAsync(WorkerConstant.Queues.StatusUpdates);
        }
        catch
        {
            // Ignore purge errors
        }
    }

    private StatusUpdatePublisher CreateStatusUpdatePublisher()
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

        return new StatusUpdatePublisher(options, _serviceProvider.GetRequiredService<ILoggerFactory>());
    }

    private async Task<(IChannel, IConnection)> SetupStatusUpdateConsumerAsync(Action<JobStatusUpdateMessage> onMessage, CancellationToken cancellationToken)
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
            queue: WorkerConstant.Queues.StatusUpdates,
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
                var message = JsonSerializer.Deserialize<JobStatusUpdateMessage>(json, _jsonOptions);

                onMessage(message);

                await channel.BasicAckAsync(ea.DeliveryTag, false);
            }
            catch
            {
                // Ignore errors during message processing
            }
        };

        await channel.BasicConsumeAsync(
            queue: WorkerConstant.Queues.StatusUpdates,
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
