using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Interfaces.Redis;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.Models.Options;
using Milvaion.Infrastructure.BackgroundServices;
using Milvaion.Infrastructure.Services.RabbitMQ;
using Milvaion.Infrastructure.Telemetry;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Models;
using Milvasoft.Milvaion.Sdk.Utils;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.BackgroundServices;

/// <summary>
/// Integration tests for WorkerAutoDiscoveryService.
/// Tests worker registration and heartbeat message consumption from RabbitMQ.
/// </summary>
[Collection(nameof(ServicesTestCollection))]
public class WorkerAutoDiscoveryServiceTests(ServicesWebApplicationFactory factory, ITestOutputHelper output) : BackgroundServiceTestBase(factory, output)
{
    [Fact]
    public async Task ProcessRegistration_ShouldRegisterWorkerInRedis()
    {
        // Arrange
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var uniqueWorkerId = $"worker-{Guid.CreateVersion7():N}";
        var uniqueInstanceId = $"{uniqueWorkerId}-inst1";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act - Start the discovery service first
        var discoveryService = CreateWorkerAutoDiscoveryService();
        _ = Task.Run(async () =>
        {
            try
            {
                await discoveryService.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(3000, cts.Token);

        // Publish worker registration message
        await PublishRegistrationMessageAsync(new WorkerDiscoveryRequest
        {
            WorkerId = uniqueWorkerId,
            InstanceId = uniqueInstanceId,
            DisplayName = "Test Worker",
            HostName = "test-host",
            IpAddress = "127.0.0.1",
            JobTypes = ["TestJob", "EmailJob"],
            MaxParallelJobs = 5,
            Version = "1.0.0"
        }, cts.Token);

        // Wait for worker to be registered in Redis
        var redisWorkerService = _serviceProvider.GetRequiredService<IRedisWorkerService>();
        var found = await WaitForConditionAsync(
            async () =>
            {
                var worker = await redisWorkerService.GetWorkerAsync(uniqueWorkerId, cts.Token);
                return worker != null;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await discoveryService.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("worker should be registered in Redis");

        var registeredWorker = await redisWorkerService.GetWorkerAsync(uniqueWorkerId, cts.Token);
        registeredWorker.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessRegistration_ShouldStoreWorkerJobTypes()
    {
        // Arrange
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var uniqueWorkerId = $"worker-jobtypes-{Guid.CreateVersion7():N}";
        var uniqueInstanceId = $"{uniqueWorkerId}-inst1";
        var jobTypes = new List<string> { "EmailJob", "ReportJob", "DataSyncJob" };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var discoveryService = CreateWorkerAutoDiscoveryService();
        _ = Task.Run(async () =>
        {
            try
            {
                await discoveryService.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(3000, cts.Token);

        // Act
        await PublishRegistrationMessageAsync(new WorkerDiscoveryRequest
        {
            WorkerId = uniqueWorkerId,
            InstanceId = uniqueInstanceId,
            DisplayName = "Job Types Worker",
            HostName = "test-host",
            IpAddress = "127.0.0.1",
            JobTypes = jobTypes,
            MaxParallelJobs = 10,
            Version = "2.0.0"
        }, cts.Token);

        var redisWorkerService = _serviceProvider.GetRequiredService<IRedisWorkerService>();
        var found = await WaitForConditionAsync(
            async () =>
            {
                var isActive = await redisWorkerService.IsWorkerActiveAsync(uniqueWorkerId, cts.Token);
                return isActive;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await discoveryService.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("worker should be active in Redis after registration");

        var isWorkerActive = await redisWorkerService.IsWorkerActiveAsync(uniqueWorkerId, cts.Token);
        isWorkerActive.Should().BeTrue();
    }

    [Fact]
    public async Task ProcessHeartbeat_ShouldUpdateWorkerHeartbeat()
    {
        // Arrange
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var uniqueWorkerId = $"worker-hb-{Guid.CreateVersion7():N}";
        var uniqueInstanceId = $"{uniqueWorkerId}-inst1";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // First register the worker via Redis directly
        var redisWorkerService = _serviceProvider.GetRequiredService<IRedisWorkerService>();
        await redisWorkerService.RegisterWorkerAsync(new WorkerDiscoveryRequest
        {
            WorkerId = uniqueWorkerId,
            InstanceId = uniqueInstanceId,
            DisplayName = "Heartbeat Test Worker",
            HostName = "test-host",
            IpAddress = "127.0.0.1",
            JobTypes = ["TestJob"],
            MaxParallelJobs = 5,
            Version = "1.0.0"
        }, cts.Token);

        // Start the discovery service
        var discoveryService = CreateWorkerAutoDiscoveryService();
        _ = Task.Run(async () =>
        {
            try
            {
                await discoveryService.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(3000, cts.Token);

        // Act - Publish heartbeat message
        await PublishHeartbeatMessageAsync(new WorkerHeartbeatMessage
        {
            WorkerId = uniqueWorkerId,
            InstanceId = uniqueInstanceId,
            CurrentJobs = 3,
            Timestamp = DateTime.UtcNow
        }, cts.Token);

        // Wait for heartbeat to be processed (batch processes every 100ms)
        await Task.Delay(2000, cts.Token);

        await discoveryService.StopAsync(cts.Token);

        // Assert - Worker should still be active after heartbeat
        var isActive = await redisWorkerService.IsWorkerActiveAsync(uniqueWorkerId, cts.Token);
        isActive.Should().BeTrue("worker should remain active after heartbeat");
    }

    [Fact]
    public async Task ProcessRegistration_ShouldHandleMultipleWorkers()
    {
        // Arrange
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var workerIds = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            workerIds.Add($"worker-multi-{i}-{Guid.CreateVersion7():N}");
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var discoveryService = CreateWorkerAutoDiscoveryService();
        _ = Task.Run(async () =>
        {
            try
            {
                await discoveryService.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(3000, cts.Token);

        // Act - Register multiple workers
        foreach (var workerId in workerIds)
        {
            await PublishRegistrationMessageAsync(new WorkerDiscoveryRequest
            {
                WorkerId = workerId,
                InstanceId = $"{workerId}-inst1",
                DisplayName = $"Multi Worker {workerId}",
                HostName = "test-host",
                IpAddress = "127.0.0.1",
                JobTypes = ["TestJob"],
                MaxParallelJobs = 5,
                Version = "1.0.0"
            }, cts.Token);
        }

        // Wait for all workers to be registered
        var redisWorkerService = _serviceProvider.GetRequiredService<IRedisWorkerService>();
        var found = await WaitForConditionAsync(
            async () =>
            {
                foreach (var workerId in workerIds)
                {
                    var worker = await redisWorkerService.GetWorkerAsync(workerId, cts.Token);
                    if (worker == null)
                        return false;
                }

                return true;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await discoveryService.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("all workers should be registered in Redis");

        foreach (var workerId in workerIds)
        {
            var isActive = await redisWorkerService.IsWorkerActiveAsync(workerId, cts.Token);
            isActive.Should().BeTrue($"worker {workerId} should be active");
        }
    }

    [Fact]
    public async Task ProcessHeartbeat_ShouldHandleMultipleHeartbeats()
    {
        // Arrange
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var uniqueWorkerId = $"worker-multi-hb-{Guid.CreateVersion7():N}";
        var uniqueInstanceId = $"{uniqueWorkerId}-inst1";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Register worker first
        var redisWorkerService = _serviceProvider.GetRequiredService<IRedisWorkerService>();
        await redisWorkerService.RegisterWorkerAsync(new WorkerDiscoveryRequest
        {
            WorkerId = uniqueWorkerId,
            InstanceId = uniqueInstanceId,
            DisplayName = "Multi Heartbeat Worker",
            HostName = "test-host",
            IpAddress = "127.0.0.1",
            JobTypes = ["TestJob"],
            MaxParallelJobs = 10,
            Version = "1.0.0"
        }, cts.Token);

        var discoveryService = CreateWorkerAutoDiscoveryService();
        _ = Task.Run(async () =>
        {
            try
            {
                await discoveryService.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(3000, cts.Token);

        // Act - Send multiple heartbeats with increasing job counts
        for (int i = 0; i < 5; i++)
        {
            await PublishHeartbeatMessageAsync(new WorkerHeartbeatMessage
            {
                WorkerId = uniqueWorkerId,
                InstanceId = uniqueInstanceId,
                CurrentJobs = i + 1,
                Timestamp = DateTime.UtcNow
            }, cts.Token);

            await Task.Delay(200, cts.Token);
        }

        // Wait for heartbeats to be processed
        await Task.Delay(2000, cts.Token);

        await discoveryService.StopAsync(cts.Token);

        // Assert - Worker should still be active
        var isActive = await redisWorkerService.IsWorkerActiveAsync(uniqueWorkerId, cts.Token);
        isActive.Should().BeTrue("worker should remain active after multiple heartbeats");
    }

    [Fact]
    public async Task ProcessRegistration_ShouldSyncRoutingPatternForOwnedJobs()
    {
        // Arrange
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var uniqueWorkerId = $"worker-routesync-{Guid.CreateVersion7():N}";
        var uniqueInstanceId = $"{uniqueWorkerId}-inst1";
        var jobTypeName = $"RouteSyncJob_{Guid.CreateVersion7():N}";
        var newPattern = $"worker.{uniqueWorkerId}";

        // Seed a job already owned by this worker but with a stale routing pattern.
        var dbContext = GetDbContext();
        var jobId = Guid.CreateVersion7();
        await dbContext.ScheduledJobs.AddAsync(new ScheduledJob
        {
            Id = jobId,
            DisplayName = jobTypeName,
            JobNameInWorker = jobTypeName,
            JobData = "{}",
            ExecuteAt = DateTime.UtcNow.AddMinutes(5),
            IsActive = true,
            WorkerId = uniqueWorkerId,
            RoutingPattern = "worker.stale-pattern",
            ConcurrentExecutionPolicy = ConcurrentExecutionPolicy.Skip,
            CreationDate = DateTime.UtcNow,
            CreatorUserName = "TestUser"
        });
        await dbContext.SaveChangesAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var discoveryService = CreateWorkerAutoDiscoveryService();
        _ = Task.Run(async () =>
        {
            try
            {
                await discoveryService.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(3000, cts.Token);

        // Act
        await PublishRegistrationMessageAsync(new WorkerDiscoveryRequest
        {
            WorkerId = uniqueWorkerId,
            InstanceId = uniqueInstanceId,
            DisplayName = "Route Sync Worker",
            HostName = "test-host",
            IpAddress = "127.0.0.1",
            JobTypes = [jobTypeName],
            RoutingPatterns = new Dictionary<string, string> { [jobTypeName] = newPattern },
            MaxParallelJobs = 5,
            Version = "1.0.0"
        }, cts.Token);

        // Assert - the owned job's RoutingPattern should be updated to the new pattern.
        var updated = await WaitForConditionAsync(
            async () =>
            {
                var freshContext = GetDbContext();
                var job = await freshContext.ScheduledJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, cts.Token);
                return job?.RoutingPattern == newPattern;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await discoveryService.StopAsync(cts.Token);

        updated.Should().BeTrue("the routing pattern of the worker's own job should be synced on registration");
    }

    [Fact]
    public async Task ProcessRegistration_ShouldReassignJobsFromInactiveWorker()
    {
        // Arrange
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var inactiveWorkerId = $"worker-inactive-{Guid.CreateVersion7():N}";
        var newWorkerId = $"worker-active-{Guid.CreateVersion7():N}";
        var newInstanceId = $"{newWorkerId}-inst1";
        var jobTypeName = $"ReassignJob_{Guid.CreateVersion7():N}";
        var newPattern = $"worker.{newWorkerId}";

        // Seed a job owned by a worker that is NOT present in Redis (i.e. inactive).
        var dbContext = GetDbContext();
        var jobId = Guid.CreateVersion7();
        await dbContext.ScheduledJobs.AddAsync(new ScheduledJob
        {
            Id = jobId,
            DisplayName = jobTypeName,
            JobNameInWorker = jobTypeName,
            JobData = "{}",
            ExecuteAt = DateTime.UtcNow.AddMinutes(5),
            IsActive = true,
            WorkerId = inactiveWorkerId,
            RoutingPattern = $"worker.{inactiveWorkerId}",
            ConcurrentExecutionPolicy = ConcurrentExecutionPolicy.Skip,
            CreationDate = DateTime.UtcNow,
            CreatorUserName = "TestUser"
        });
        await dbContext.SaveChangesAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var discoveryService = CreateWorkerAutoDiscoveryService();
        _ = Task.Run(async () =>
        {
            try
            {
                await discoveryService.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(3000, cts.Token);

        // Act - a new, active worker registers and advertises the same job type.
        await PublishRegistrationMessageAsync(new WorkerDiscoveryRequest
        {
            WorkerId = newWorkerId,
            InstanceId = newInstanceId,
            DisplayName = "Reassign Target Worker",
            HostName = "test-host",
            IpAddress = "127.0.0.1",
            JobTypes = [jobTypeName],
            RoutingPatterns = new Dictionary<string, string> { [jobTypeName] = newPattern },
            MaxParallelJobs = 5,
            Version = "1.0.0"
        }, cts.Token);

        // Assert - the stale job should be reassigned to the new worker with the new pattern.
        var reassigned = await WaitForConditionAsync(
            async () =>
            {
                var freshContext = GetDbContext();
                var job = await freshContext.ScheduledJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, cts.Token);
                return job?.WorkerId == newWorkerId && job.RoutingPattern == newPattern;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await discoveryService.StopAsync(cts.Token);

        reassigned.Should().BeTrue("jobs stranded on an inactive worker should be reassigned to the newly registered worker");
    }

    private WorkerAutoDiscoveryService CreateWorkerAutoDiscoveryService() => new(
            _serviceProvider.GetRequiredService<IRedisWorkerService>(),
            _serviceProvider.GetRequiredService<RabbitMQConnectionFactory>(),
            Options.Create(new WorkerAutoDiscoveryOptions
            {
                Enabled = true
            }),
            _serviceProvider.GetRequiredService<IOptions<RabbitMQOptions>>(),
            _serviceProvider.GetRequiredService<IAlertNotifier>(),
            _serviceProvider.GetRequiredService<ILoggerFactory>(),
            _serviceProvider,
            _serviceProvider.GetRequiredService<BackgroundServiceMetrics>()
        );

    private async Task PublishRegistrationMessageAsync(WorkerDiscoveryRequest message, CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = _factory.GetRabbitMqHost(),
            Port = _factory.GetRabbitMqPort(),
            UserName = "guest",
            Password = "guest"
        };

        await using var connection = await factory.CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            queue: WorkerConstant.Queues.WorkerRegistration,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, ConstantJsonOptions.PropNameCaseInsensitive));

        var properties = new BasicProperties
        {
            Persistent = true
        };

        await channel.BasicPublishAsync(
            exchange: "",
            routingKey: WorkerConstant.Queues.WorkerRegistration,
            mandatory: false,
            basicProperties: properties,
            body: body,
            cancellationToken: cancellationToken);
    }

    private async Task PublishHeartbeatMessageAsync(WorkerHeartbeatMessage message, CancellationToken cancellationToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = _factory.GetRabbitMqHost(),
            Port = _factory.GetRabbitMqPort(),
            UserName = "guest",
            Password = "guest"
        };

        await using var connection = await factory.CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            queue: WorkerConstant.Queues.WorkerHeartbeat,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, ConstantJsonOptions.PropNameCaseInsensitive));

        var properties = new BasicProperties
        {
            Persistent = true
        };

        await channel.BasicPublishAsync(
            exchange: "",
            routingKey: WorkerConstant.Queues.WorkerHeartbeat,
            mandatory: false,
            basicProperties: properties,
            body: body,
            cancellationToken: cancellationToken);
    }
}
