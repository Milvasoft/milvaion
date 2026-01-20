using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Models;
using Milvasoft.Milvaion.Sdk.Utils;
using Milvasoft.Milvaion.Sdk.Worker.Core;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using Milvasoft.Milvaion.Sdk.Worker.RabbitMQ;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.WorkerSdk;

/// <summary>
/// Integration tests for WorkerListenerPublisher.
/// Tests worker registration and heartbeat publishing to RabbitMQ.
/// </summary>
[Collection(nameof(MilvaionTestCollection))]
public class WorkerListenerPublisherTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task WorkerRegistration_ShouldPublishRegistrationOnStartup()
    {
        // Arrange
        await InitializeAsync();

        var uniqueWorkerId = $"test-worker-{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        WorkerDiscoveryRequest receivedRegistration = null;
        (IChannel channel, IConnection connection) = await SetupRegistrationConsumerAsync(msg =>
       {
           if (msg?.WorkerId == uniqueWorkerId)
               receivedRegistration = msg;
       }, cts.Token);

        // Act - Start service first
        var options = CreateWorkerOptions(uniqueWorkerId);
        var service = CreateWorkerListenerPublisher(options: options);
        _ = Task.Run(async () =>
        {
            try
            {
                await service.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait for registration
        var found = await WaitForConditionAsync(
            () => Task.FromResult(receivedRegistration != null),
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(300),
            cancellationToken: cts.Token);

        await service.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("registration message should be received");
        receivedRegistration.Should().NotBeNull();
        receivedRegistration!.WorkerId.Should().Be(uniqueWorkerId);
        receivedRegistration.JobTypes.Should().Contain("TestJob");
        receivedRegistration.MaxParallelJobs.Should().BeGreaterThan(0);

        await PurgeQueuesAsync();
        await channel.CloseAsync();
        await connection.CloseAsync();
    }

    [Fact]
    public async Task WorkerRegistration_ShouldIncludeAllJobTypes()
    {
        // Arrange
        await InitializeAsync();

        var uniqueWorkerId = $"test-worker-{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        WorkerDiscoveryRequest receivedRegistration = null;
        (IChannel channel, IConnection connection) = await SetupRegistrationConsumerAsync(msg =>
       {
           if (msg?.WorkerId == uniqueWorkerId)
               receivedRegistration = msg;
       }, cts.Token);

        var jobConfigs = new Dictionary<string, JobConsumerConfig>
        {
            ["TestJob"] = new JobConsumerConfig { ConsumerId = "consumer-1", RoutingPattern = "test.*" },
            ["EmailJob"] = new JobConsumerConfig { ConsumerId = "consumer-2", RoutingPattern = "email.*" },
            ["ReportJob"] = new JobConsumerConfig { ConsumerId = "consumer-3", RoutingPattern = "report.*" }
        };

        // Act
        var options = CreateWorkerOptions(uniqueWorkerId);
        var service = CreateWorkerListenerPublisher(jobConfigs, options);
        _ = Task.Run(async () =>
        {
            try
            {
                await service.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait for registration
        var found = await WaitForConditionAsync(
            () => Task.FromResult(receivedRegistration != null),
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(300),
            cancellationToken: cts.Token);

        await service.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("registration message should be received");
        receivedRegistration.Should().NotBeNull();
        receivedRegistration!.JobTypes.Should().HaveCount(3);
        receivedRegistration.JobTypes.Should().Contain("TestJob");
        receivedRegistration.JobTypes.Should().Contain("EmailJob");
        receivedRegistration.JobTypes.Should().Contain("ReportJob");

        await PurgeQueuesAsync();
        await channel.CloseAsync();
        await connection.CloseAsync();
    }

    [Fact]
    public async Task WorkerRegistration_ShouldIncludeRoutingPatterns()
    {
        // Arrange
        await InitializeAsync();

        var uniqueWorkerId = $"test-worker-{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        WorkerDiscoveryRequest receivedRegistration = null;
        (IChannel channel, IConnection connection) = await SetupRegistrationConsumerAsync(msg =>
        {
            if (msg?.WorkerId == uniqueWorkerId)
                receivedRegistration = msg;
        }, cts.Token);

        var jobConfigs = new Dictionary<string, JobConsumerConfig>
        {
            ["TestJob"] = new JobConsumerConfig { ConsumerId = "consumer-1", RoutingPattern = "test.routing.pattern" }
        };

        // Act
        var options = CreateWorkerOptions(uniqueWorkerId);
        var service = CreateWorkerListenerPublisher(jobConfigs, options);
        _ = Task.Run(async () =>
        {
            try
            {
                await service.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait for registration
        var found = await WaitForConditionAsync(
            () => Task.FromResult(receivedRegistration != null),
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(300),
            cancellationToken: cts.Token);

        await service.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("registration message should be received");
        receivedRegistration.Should().NotBeNull();
        receivedRegistration!.RoutingPatterns.Should().NotBeEmpty();
        receivedRegistration.RoutingPatterns.Should().ContainKey("TestJob");
        receivedRegistration.RoutingPatterns["TestJob"].Should().Be("test.routing.pattern");

        await PurgeQueuesAsync();
        await channel.CloseAsync();
        await connection.CloseAsync();
    }

    [Fact]
    public async Task WorkerHeartbeat_ShouldPublishPeriodically()
    {
        // Arrange
        await InitializeAsync();

        var uniqueWorkerId = $"test-worker-{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var receivedHeartbeats = new List<WorkerHeartbeatMessage>();
        (IChannel channel, IConnection connection) = await SetupHeartbeatConsumerAsync(msg =>
        {
            if (msg?.WorkerId == uniqueWorkerId)
                receivedHeartbeats.Add(msg);
        }, cts.Token);

        // Create options with short heartbeat interval for testing
        var options = CreateWorkerOptions(uniqueWorkerId);
        options.Heartbeat.IntervalSeconds = 2;

        // Act
        var service = CreateWorkerListenerPublisher(options: options);
        _ = Task.Run(async () =>
        {
            try
            {
                await service.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait for at least one heartbeat
        var found = await WaitForConditionAsync(
            () => Task.FromResult(receivedHeartbeats.Count >= 1),
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(300),
            cancellationToken: cts.Token);

        await service.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("heartbeat messages should be received");
        receivedHeartbeats.Should().HaveCountGreaterOrEqualTo(1);
        receivedHeartbeats.All(h => h.WorkerId == uniqueWorkerId).Should().BeTrue();

        await PurgeQueuesAsync();
        await channel.CloseAsync();
        await connection.CloseAsync();
    }

    [Fact]
    public async Task WorkerHeartbeat_ShouldIncludeCurrentJobCount()
    {
        // Arrange
        await InitializeAsync();

        var uniqueWorkerId = $"test-worker-{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var receivedHeartbeats = new List<WorkerHeartbeatMessage>();
        (IChannel channel, IConnection connection) = await SetupHeartbeatConsumerAsync(msg =>
        {
            if (msg?.WorkerId == uniqueWorkerId)
                receivedHeartbeats.Add(msg);
        }, cts.Token);

        var options = CreateWorkerOptions(uniqueWorkerId);
        options.Heartbeat.IntervalSeconds = 1;

        // Act
        var service = CreateWorkerListenerPublisher(options: options);
        _ = Task.Run(async () =>
        {
            try
            {
                await service.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait for at least one heartbeat
        var found = await WaitForConditionAsync(
            () => Task.FromResult(receivedHeartbeats.Count >= 1),
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(300),
            cancellationToken: cts.Token);

        await service.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("heartbeat message should be received");
        receivedHeartbeats.Should().NotBeEmpty();
        var heartbeat = receivedHeartbeats.First();
        heartbeat.CurrentJobs.Should().BeGreaterOrEqualTo(0);
        heartbeat.Timestamp.Should().BeAfter(DateTime.UtcNow.AddMinutes(-1));

        await PurgeQueuesAsync();
        await channel.CloseAsync();
        await connection.CloseAsync();
    }

    [Fact]
    public async Task WorkerRegistration_ShouldIncludeInstanceId()
    {
        // Arrange
        await InitializeAsync();

        var uniqueWorkerId = $"test-worker-{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        WorkerDiscoveryRequest receivedRegistration = null;
        (IChannel channel, IConnection connection) = await SetupRegistrationConsumerAsync(msg =>
       {
           if (msg?.WorkerId == uniqueWorkerId)
               receivedRegistration = msg;
       }, cts.Token);

        // Act
        var options = CreateWorkerOptions(uniqueWorkerId);
        var service = CreateWorkerListenerPublisher(options: options);
        _ = Task.Run(async () =>
        {
            try
            {
                await service.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait for registration
        var found = await WaitForConditionAsync(
            () => Task.FromResult(receivedRegistration != null),
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(300),
            cancellationToken: cts.Token);

        await service.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("registration message should be received");
        receivedRegistration.Should().NotBeNull();
        receivedRegistration!.InstanceId.Should().NotBeNullOrEmpty();
        receivedRegistration.InstanceId.Should().StartWith(uniqueWorkerId + "-");

        await PurgeQueuesAsync();
        await channel.CloseAsync();
        await connection.CloseAsync();
    }

    [Fact]
    public async Task WorkerRegistration_ShouldIncludeMetadata()
    {
        // Arrange
        await InitializeAsync();

        var uniqueWorkerId = $"test-worker-{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        WorkerDiscoveryRequest receivedRegistration = null;
        (IChannel channel, IConnection connection) = await SetupRegistrationConsumerAsync(msg =>
       {
           if (msg?.WorkerId == uniqueWorkerId)
               receivedRegistration = msg;
       }, cts.Token);

        // Act
        var options = CreateWorkerOptions(uniqueWorkerId);
        var service = CreateWorkerListenerPublisher(options: options);
        _ = Task.Run(async () =>
        {
            try
            {
                await service.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait for registration
        var found = await WaitForConditionAsync(
            () => Task.FromResult(receivedRegistration != null),
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(300),
            cancellationToken: cts.Token);

        await service.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("registration message should be received");
        receivedRegistration.Should().NotBeNull();
        receivedRegistration!.Metadata.Should().NotBeNullOrEmpty();
        receivedRegistration.Metadata.Should().Contain("ProcessorCount");

        await PurgeQueuesAsync();
        await channel.CloseAsync();
        await connection.CloseAsync();
    }

    private WorkerListenerPublisher CreateWorkerListenerPublisher(Dictionary<string, JobConsumerConfig> jobConfigs = null, WorkerOptions options = null)
    {
        options ??= CreateWorkerOptions();
        jobConfigs ??= new Dictionary<string, JobConsumerConfig>
        {
            ["TestJob"] = new JobConsumerConfig { ConsumerId = "consumer-1", RoutingPattern = "test.*" }
        };

        // Create a mock service provider with WorkerJobTracker
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<WorkerJobTracker>();
        services.AddSingleton(Options.Create(options));
        var mockServiceProvider = services.BuildServiceProvider();

        return new WorkerListenerPublisher(
            Options.Create(options),
            _serviceProvider.GetRequiredService<IMilvaLogger>(),
            mockServiceProvider,
            jobConfigs
        );
    }

    private WorkerOptions CreateWorkerOptions(string workerId = "test-worker")
    {
        var options = new WorkerOptions
        {
            WorkerId = workerId,
            MaxParallelJobs = 4,
            RabbitMQ = new RabbitMQSettings
            {
                Host = _factory.GetRabbitMqHost(),
                Port = _factory.GetRabbitMqPort(),
                Username = "guest",
                Password = "guest",
                VirtualHost = "/"
            },
            Heartbeat = new HeartbeatSettings
            {
                IntervalSeconds = 1
            }
        };

        options.RegenerateInstanceId();
        return options;
    }

    private async Task<(IChannel, IConnection)> SetupRegistrationConsumerAsync(Action<WorkerDiscoveryRequest> onMessage, CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = _factory.GetRabbitMqHost(),
            Port = _factory.GetRabbitMqPort(),
            UserName = "guest",
            Password = "guest"
        };

        // Don't use 'using' - connection needs to stay alive for consumer to work
        // Connection will be closed when cancellationToken is cancelled
        var connection = await factory.CreateConnectionAsync(cancellationToken);
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            queue: WorkerConstant.Queues.WorkerRegistration,
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
                var message = JsonSerializer.Deserialize<WorkerDiscoveryRequest>(json, _jsonOptions);

                onMessage(message);

                await channel.BasicAckAsync(ea.DeliveryTag, false);
            }
            catch
            {
                // Ignore errors during message processing
            }
        };

        await channel.BasicConsumeAsync(
            queue: WorkerConstant.Queues.WorkerRegistration,
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

    private async Task<(IChannel, IConnection)> SetupHeartbeatConsumerAsync(Action<WorkerHeartbeatMessage> onMessage, CancellationToken cancellationToken)
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

    /// <summary>
    /// Waits for a condition to be true with timeout.
    /// </summary>
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

    /// <summary>
    /// Purges all relevant RabbitMQ queues to ensure clean test state.
    /// </summary>
    private async Task PurgeQueuesAsync()
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

            // Declare and purge queues
            var queuesToPurge = new[]
            {
                WorkerConstant.Queues.WorkerRegistration,
                WorkerConstant.Queues.WorkerHeartbeat
            };

            foreach (var queueName in queuesToPurge)
            {
                try
                {
                    await channel.QueueDeclareAsync(
                        queue: queueName,
                        durable: true,
                        exclusive: false,
                        autoDelete: false,
                        arguments: null);

                    await channel.QueuePurgeAsync(queueName);
                }
                catch
                {
                    // Queue might not exist yet, ignore
                }
            }
        }
        catch
        {
            // Ignore purge errors
        }
    }
}
