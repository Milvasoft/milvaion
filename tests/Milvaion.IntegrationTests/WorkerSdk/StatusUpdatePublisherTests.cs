using FluentAssertions;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Domain.JsonModels;
using Milvasoft.Milvaion.Sdk.Models;
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
[Collection(nameof(WorkerSdkTestCollection))]
public class StatusUpdatePublisherTests(WorkerSdkContainerFixture fixture, ITestOutputHelper output) : WorkerSdkTestBase(fixture, output)
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task PublishStatusAsync_ShouldRecover_AfterBrokerClosesTheChannel()
    {
        // Arrange - redeclaring the existing classic queue as quorum makes the broker close the channel with a 406
        await PurgeStatusUpdatesQueueAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var options = new WorkerOptions { RabbitMQ = new RabbitMQSettings { Host = GetRabbitMqHost(), Port = GetRabbitMqPort(), QueueType = RabbitMQQueueType.Quorum } };

        await using var publisher = new StatusUpdatePublisher(options, GetLoggerFactory());

        var rabbitFactory = new ConnectionFactory { HostName = GetRabbitMqHost(), Port = GetRabbitMqPort(), UserName = "guest", Password = "guest" };

        await using var connection = await rabbitFactory.CreateConnectionAsync(cts.Token);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cts.Token);

        Task Publish() => publisher.PublishStatusAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), "worker", "instance", JobOccurrenceStatus.Running, cancellationToken: cts.Token);

        await FluentActions.Awaiting(Publish).Should().ThrowAsync<Exception>();

        try
        {
            // Act - once the cause is gone, the same publisher must work again
            await channel.QueueDeleteAsync(WorkerConstant.Queues.StatusUpdates, ifUnused: false, ifEmpty: false, cancellationToken: cts.Token);

            await Publish();

            // Assert
            var message = await channel.BasicGetAsync(WorkerConstant.Queues.StatusUpdates, autoAck: true, cts.Token);

            message.Should().NotBeNull();
            message!.BasicProperties.Persistent.Should().BeTrue("a durable queue only keeps persistent messages across a broker restart");
        }
        finally
        {
            // The other tests expect the classic queue
            await channel.QueueDeleteAsync(WorkerConstant.Queues.StatusUpdates, ifUnused: false, ifEmpty: false, cancellationToken: CancellationToken.None);
        }
    }

    [Fact]
    public async Task PublishStatusAsync_ShouldPublishRunningStatus()
    {
        // Arrange

        await PurgeStatusUpdatesQueueAsync();

        var correlationId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var workerId = $"test-worker-{Guid.CreateVersion7():N}";
        var startTime = DateTime.UtcNow;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        JobStatusUpdateMessage receivedMessage = null;
        (IChannel channel, IConnection connection) = await SetupStatusUpdateConsumerAsync(msg =>
        {
            if (msg?.OccurrenceId == correlationId)
                receivedMessage = msg;
        }, cts.Token);

        await using var publisher = CreateStatusUpdatePublisher();

        // Act
        await publisher.PublishStatusAsync(
            occurrenceId: correlationId,
            jobId: jobId,
            workerId: workerId,
            instanceId: $"{workerId}-instance",
            status: JobOccurrenceStatus.Running,
            startTime: startTime,
            cancellationToken: cts.Token);

        // Wait for message
        var found = await WaitForConditionAsync(
            () => Task.FromResult(receivedMessage != null),
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(300),
            cancellationToken: cts.Token);

        await channel.CloseAsync();
        await connection.CloseAsync();

        // Assert
        found.Should().BeTrue("status update message should be received");
        receivedMessage.Should().NotBeNull();
        receivedMessage!.OccurrenceId.Should().Be(correlationId);
        receivedMessage.JobId.Should().Be(jobId);
        receivedMessage.WorkerId.Should().Be(workerId);
        receivedMessage.Status.Should().Be(JobOccurrenceStatus.Running);
        receivedMessage.StartTime.Should().NotBeNull();
    }

    [Fact]
    public async Task PublishStatusAsync_ShouldPublishCompletedStatusWithResult()
    {
        // Arrange

        await PurgeStatusUpdatesQueueAsync();

        var correlationId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var workerId = $"test-worker-{Guid.CreateVersion7():N}";
        var startTime = DateTime.UtcNow.AddSeconds(-10);
        var endTime = DateTime.UtcNow;
        var durationMs = 10000L;
        var result = $"Job completed successfully - {Guid.CreateVersion7():N}";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        JobStatusUpdateMessage receivedMessage = null;
        (IChannel channel, IConnection connection) = await SetupStatusUpdateConsumerAsync(msg =>
        {
            if (msg?.OccurrenceId == correlationId)
                receivedMessage = msg;
        }, cts.Token);

        await using var publisher = CreateStatusUpdatePublisher();

        // Act
        await publisher.PublishStatusAsync(
            occurrenceId: correlationId,
            jobId: jobId,
            workerId: workerId,
            instanceId: $"{workerId}-instance",
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

        await channel.CloseAsync();
        await connection.CloseAsync();

        // Assert
        found.Should().BeTrue("completed status message should be received");
        receivedMessage.Should().NotBeNull();
        receivedMessage!.Status.Should().Be(JobOccurrenceStatus.Completed);
        receivedMessage.EndTime.Should().NotBeNull();
        receivedMessage.DurationMs.Should().Be(durationMs);
        receivedMessage.Result.Should().Be(result);
    }

    [Fact]
    public async Task PublishStatusAsync_ShouldPublishFailedStatusWithException()
    {
        // Arrange

        await PurgeStatusUpdatesQueueAsync();

        var correlationId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var workerId = $"test-worker-{Guid.CreateVersion7():N}";
        var exception = $"NullReferenceException: {Guid.CreateVersion7():N}";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        JobStatusUpdateMessage receivedMessage = null;
        (IChannel channel, IConnection connection) = await SetupStatusUpdateConsumerAsync(msg =>
        {
            if (msg?.OccurrenceId == correlationId)
                receivedMessage = msg;
        }, cts.Token);

        await using var publisher = CreateStatusUpdatePublisher();

        // Act
        await publisher.PublishStatusAsync(
            occurrenceId: correlationId,
            jobId: jobId,
            workerId: workerId,
            instanceId: $"{workerId}-instance",
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

        await channel.CloseAsync();
        await connection.CloseAsync();

        // Assert
        found.Should().BeTrue("failed status message should be received");
        receivedMessage.Should().NotBeNull();
        receivedMessage!.Status.Should().Be(JobOccurrenceStatus.Failed);
        receivedMessage.Exception.Should().Contain("NullReferenceException");
    }

    [Fact]
    public async Task PublishStatusAsync_ShouldPublishCancelledStatus()
    {
        // Arrange

        await PurgeStatusUpdatesQueueAsync();

        var correlationId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var workerId = $"test-worker-{Guid.CreateVersion7():N}";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        JobStatusUpdateMessage receivedMessage = null;
        (IChannel channel, IConnection connection) = await SetupStatusUpdateConsumerAsync(msg =>
        {
            if (msg?.OccurrenceId == correlationId)
                receivedMessage = msg;
        }, cts.Token);

        await using var publisher = CreateStatusUpdatePublisher();

        // Act
        await publisher.PublishStatusAsync(
            occurrenceId: correlationId,
            jobId: jobId,
            workerId: workerId,
            instanceId: $"{workerId}-instance",
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

        await channel.CloseAsync();
        await connection.CloseAsync();

        // Assert
        found.Should().BeTrue("cancelled status message should be received");
        receivedMessage.Should().NotBeNull();
        receivedMessage!.Status.Should().Be(JobOccurrenceStatus.Cancelled);
        receivedMessage.Result.Should().Contain("cancelled");
    }

    [Fact]
    public async Task PublishStatusAsync_ShouldPublishTimedOutStatus()
    {
        // Arrange

        await PurgeStatusUpdatesQueueAsync();

        var correlationId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var workerId = $"test-worker-{Guid.CreateVersion7():N}";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        JobStatusUpdateMessage receivedMessage = null;
        (IChannel channel, IConnection connection) = await SetupStatusUpdateConsumerAsync(msg =>
        {
            if (msg?.OccurrenceId == correlationId)
                receivedMessage = msg;
        }, cts.Token);

        await using var publisher = CreateStatusUpdatePublisher();

        // Act
        await publisher.PublishStatusAsync(
            occurrenceId: correlationId,
            jobId: jobId,
            workerId: workerId,
            instanceId: $"{workerId}-instance",
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

        await channel.CloseAsync();
        await connection.CloseAsync();

        // Assert
        found.Should().BeTrue("timed out status message should be received");
        receivedMessage.Should().NotBeNull();
        receivedMessage!.Status.Should().Be(JobOccurrenceStatus.TimedOut);
        receivedMessage.Exception.Should().Contain("timed out");
    }

    [Fact]
    public async Task PublishStatusAsync_ShouldIncludeMessageTimestamp()
    {
        // Arrange

        await PurgeStatusUpdatesQueueAsync();

        var correlationId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var beforePublish = DateTime.UtcNow;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        JobStatusUpdateMessage receivedMessage = null;
        (IChannel channel, IConnection connection) = await SetupStatusUpdateConsumerAsync(msg =>
        {
            if (msg?.OccurrenceId == correlationId)
                receivedMessage = msg;
        }, cts.Token);

        await using var publisher = CreateStatusUpdatePublisher();

        // Act
        await publisher.PublishStatusAsync(
            occurrenceId: correlationId,
            jobId: jobId,
            workerId: "test-worker",
            instanceId: "test-worker-instance",
            status: JobOccurrenceStatus.Running,
            cancellationToken: cts.Token);

        // Wait for message
        var found = await WaitForConditionAsync(
            () => Task.FromResult(receivedMessage != null),
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(300),
            cancellationToken: cts.Token);

        await channel.CloseAsync();
        await connection.CloseAsync();

        // Assert
        found.Should().BeTrue("status message should be received");
        receivedMessage.Should().NotBeNull();
        receivedMessage!.MessageTimestamp.Should().BeAfter(beforePublish.AddSeconds(-1));
        receivedMessage.MessageTimestamp.Should().BeBefore(DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task PublishShutdownHeartbeatAsync_ShouldPublishHeartbeatWithStoppingFlag()
    {
        // Arrange
        await PurgeHeartbeatQueueAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        WorkerHeartbeatMessage receivedMessage = null;
        (IChannel channel, IConnection connection) = await SetupHeartbeatConsumerAsync(msg =>
        {
            if (msg?.IsStopping == true)
                receivedMessage = msg;
        }, cts.Token);

        await using var publisher = CreateStatusUpdatePublisher();

        // Act
        await publisher.PublishShutdownHeartbeatAsync(cts.Token);

        // Wait for message
        var found = await WaitForConditionAsync(
            () => Task.FromResult(receivedMessage != null),
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(300),
            cancellationToken: cts.Token);

        await channel.CloseAsync();
        await connection.CloseAsync();

        // Assert
        found.Should().BeTrue("shutdown heartbeat message should be received");
        receivedMessage.Should().NotBeNull();
        receivedMessage!.IsStopping.Should().BeTrue();
        receivedMessage.CurrentJobs.Should().Be(0);
        receivedMessage.WorkerId.Should().Be("test-worker");
    }

    [Fact]
    public async Task DisposeAsync_ShouldNotThrow_WhenNoConnectionEstablished()
    {
        // Arrange - Create publisher but don't publish anything
        await using var publisher = CreateStatusUpdatePublisher();

        // Act & Assert - Should not throw
        var act = async () => await publisher.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DisposeAsync_ShouldNotThrow_WhenCalledAfterPublish()
    {
        // Arrange
        await using var publisher = CreateStatusUpdatePublisher();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await publisher.PublishStatusAsync(
            occurrenceId: Guid.CreateVersion7(),
            jobId: Guid.CreateVersion7(),
            workerId: "test-worker",
            instanceId: "test-worker-instance",
            status: JobOccurrenceStatus.Running,
            cancellationToken: cts.Token);

        // Act & Assert
        var act = async () => await publisher.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PublishShutdownHeartbeatAsync_ShouldNotThrow_WhenCancelled()
    {
        // Arrange
        await using var publisher = CreateStatusUpdatePublisher();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert - Shutdown heartbeat should handle cancellation gracefully
        var act = async () => await publisher.PublishShutdownHeartbeatAsync(cts.Token);
        await act.Should().NotThrowAsync();
    }

    private async Task PurgeStatusUpdatesQueueAsync()
    {
        try
        {
            var factory = new ConnectionFactory
            {
                HostName = GetRabbitMqHost(),
                Port = GetRabbitMqPort(),
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
                Host = GetRabbitMqHost(),
                Port = GetRabbitMqPort(),
                Username = "guest",
                Password = "guest",
                VirtualHost = "/"
            }
        };

        return new StatusUpdatePublisher(options, GetLoggerFactory());
    }

    private async Task<(IChannel, IConnection)> SetupStatusUpdateConsumerAsync(Action<JobStatusUpdateMessage> onMessage, CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = GetRabbitMqHost(),
            Port = GetRabbitMqPort(),
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

    private async Task PurgeHeartbeatQueueAsync()
    {
        try
        {
            var factory = new ConnectionFactory
            {
                HostName = GetRabbitMqHost(),
                Port = GetRabbitMqPort(),
                UserName = "guest",
                Password = "guest"
            };

            await using var connection = await factory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();

            await channel.QueueDeclareAsync(
                queue: WorkerConstant.Queues.WorkerHeartbeat,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null);

            await channel.QueuePurgeAsync(WorkerConstant.Queues.WorkerHeartbeat);
        }
        catch
        {
            // Ignore purge errors
        }
    }

    private async Task<(IChannel, IConnection)> SetupHeartbeatConsumerAsync(Action<WorkerHeartbeatMessage> onMessage, CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = GetRabbitMqHost(),
            Port = GetRabbitMqPort(),
            UserName = "guest",
            Password = "guest"
        };

        var connection = await factory.CreateConnectionAsync(cancellationToken);
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            queue: WorkerConstant.Queues.WorkerHeartbeat,
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
                var message = JsonSerializer.Deserialize<WorkerHeartbeatMessage>(json, _jsonOptions);

                onMessage(message);

                await channel.BasicAckAsync(ea.DeliveryTag, false);
            }
            catch
            {
                // Ignore errors during message processing
            }
        };

        await channel.BasicConsumeAsync(
            queue: WorkerConstant.Queues.WorkerHeartbeat,
            autoAck: false,
            consumer: consumer,
            cancellationToken: cancellationToken);

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
