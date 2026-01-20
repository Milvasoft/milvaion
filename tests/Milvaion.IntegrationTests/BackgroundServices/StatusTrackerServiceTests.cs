using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces.Redis;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.Models.Options;
using Milvaion.Infrastructure.BackgroundServices;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Domain.JsonModels;
using Milvasoft.Milvaion.Sdk.Utils;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.BackgroundServices;

/// <summary>
/// Integration tests for StatusTrackerService.
/// Tests status update consumption and occurrence state management.
/// </summary>
[Collection(nameof(MilvaionTestCollection))]
public class StatusTrackerServiceTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : BackgroundServiceTestBase(factory, output)
{

    [Fact]
    public async Task ProcessStatusUpdate_ShouldUpdateOccurrenceStatus()
    {
        // Arrange
        await InitializeAsync();

        var job = await SeedScheduledJobAsync("StatusTestJob");
        var occurrence = await SeedJobOccurrenceAsync(
            jobId: job.Id,
            jobName: job.JobNameInWorker,
            status: JobOccurrenceStatus.Queued
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var uniqueWorkerId = $"test-worker-{Guid.NewGuid():N}";

        // Act - Start the tracker first
        var tracker = CreateStatusTrackerService();
        _ = Task.Run(async () =>
        {
            try
            {
                await tracker.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(1000, cts.Token);

        // Publish status update to Running
        await PublishStatusUpdateAsync(new JobStatusUpdateMessage
        {
            CorrelationId = occurrence.CorrelationId,
            JobId = job.Id,
            WorkerId = uniqueWorkerId,
            Status = JobOccurrenceStatus.Running,
            StartTime = DateTime.UtcNow
        }, cts.Token);

        // Wait for condition
        var found = await WaitForConditionAsync(
            async () =>
            {
                var occ = await GetOccurrenceAsync(occurrence.Id, cts.Token);
                return occ?.Status == JobOccurrenceStatus.Running && occ?.WorkerId == uniqueWorkerId;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await tracker.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("occurrence status should be updated to Running");

        var updatedOccurrence = await GetOccurrenceAsync(occurrence.Id, cts.Token);
        updatedOccurrence.Status.Should().Be(JobOccurrenceStatus.Running);
        updatedOccurrence.WorkerId.Should().Be(uniqueWorkerId);
        updatedOccurrence.StartTime.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessStatusUpdate_ShouldTrackStatusChangeHistory()
    {
        // Arrange
        await InitializeAsync();

        var job = await SeedScheduledJobAsync("StatusHistoryJob");
        var occurrence = await SeedJobOccurrenceAsync(
            jobId: job.Id,
            jobName: job.JobNameInWorker,
            status: JobOccurrenceStatus.Queued
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act - Start the tracker first
        var tracker = CreateStatusTrackerService();
        _ = Task.Run(async () =>
        {
            try
            {
                await tracker.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(1000, cts.Token);

        // Publish status update
        await PublishStatusUpdateAsync(new JobStatusUpdateMessage
        {
            CorrelationId = occurrence.CorrelationId,
            JobId = job.Id,
            WorkerId = "test-worker",
            Status = JobOccurrenceStatus.Running,
            StartTime = DateTime.UtcNow
        }, cts.Token);

        // Wait for condition
        var found = await WaitForConditionAsync(
            async () =>
            {
                var occ = await GetOccurrenceAsync(occurrence.Id, cts.Token);
                return occ?.StatusChangeLogs?.Any(s => s.To == JobOccurrenceStatus.Running) == true;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await tracker.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("status change log should be recorded");

        var updatedOccurrence = await GetOccurrenceAsync(occurrence.Id, cts.Token);
        updatedOccurrence.StatusChangeLogs.Should().NotBeNull();
        updatedOccurrence.StatusChangeLogs.Should().NotBeEmpty();

        var statusChange = updatedOccurrence.StatusChangeLogs.First();
        statusChange.From.Should().Be(JobOccurrenceStatus.Queued);
        statusChange.To.Should().Be(JobOccurrenceStatus.Running);
    }

    [Fact]
    public async Task ProcessStatusUpdate_ShouldHandleCompletedStatus()
    {
        // Arrange
        await InitializeAsync();

        var job = await SeedScheduledJobAsync("CompletedJob");
        var occurrence = await SeedJobOccurrenceAsync(
            jobId: job.Id,
            jobName: job.JobNameInWorker,
            status: JobOccurrenceStatus.Running
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var startTime = DateTime.UtcNow.AddSeconds(-10);
        var endTime = DateTime.UtcNow;
        var uniqueResult = $"Job completed successfully - {Guid.NewGuid():N}";

        // Act - Start the tracker first
        var tracker = CreateStatusTrackerService();
        _ = Task.Run(async () =>
        {
            try
            {
                await tracker.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(1000, cts.Token);

        await PublishStatusUpdateAsync(new JobStatusUpdateMessage
        {
            CorrelationId = occurrence.CorrelationId,
            JobId = job.Id,
            WorkerId = "test-worker",
            Status = JobOccurrenceStatus.Completed,
            StartTime = startTime,
            EndTime = endTime,
            DurationMs = 10000,
            Result = uniqueResult
        }, cts.Token);

        // Wait for condition
        var found = await WaitForConditionAsync(
            async () =>
            {
                var occ = await GetOccurrenceAsync(occurrence.Id, cts.Token);
                return occ?.Status == JobOccurrenceStatus.Completed && occ?.Result == uniqueResult;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await tracker.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("occurrence should be marked as Completed");

        var updatedOccurrence = await GetOccurrenceAsync(occurrence.Id, cts.Token);
        updatedOccurrence.Status.Should().Be(JobOccurrenceStatus.Completed);
        updatedOccurrence.EndTime.Should().NotBeNull();
        updatedOccurrence.DurationMs.Should().Be(10000);
        updatedOccurrence.Result.Should().Be(uniqueResult);
    }

    [Fact]
    public async Task ProcessStatusUpdate_ShouldHandleFailedStatus()
    {
        // Arrange
        await InitializeAsync();

        var job = await SeedScheduledJobAsync("FailedJob");
        var occurrence = await SeedJobOccurrenceAsync(
            jobId: job.Id,
            jobName: job.JobNameInWorker,
            status: JobOccurrenceStatus.Running
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var uniqueException = $"NullReferenceException: {Guid.NewGuid():N}";

        // Act - Start the tracker first
        var tracker = CreateStatusTrackerService();
        _ = Task.Run(async () =>
        {
            try
            {
                await tracker.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(1000, cts.Token);

        await PublishStatusUpdateAsync(new JobStatusUpdateMessage
        {
            CorrelationId = occurrence.CorrelationId,
            JobId = job.Id,
            WorkerId = "test-worker",
            Status = JobOccurrenceStatus.Failed,
            EndTime = DateTime.UtcNow,
            Exception = uniqueException
        }, cts.Token);

        // Wait for condition
        var found = await WaitForConditionAsync(
            async () =>
            {
                var occ = await GetOccurrenceAsync(occurrence.Id, cts.Token);
                return occ?.Status == JobOccurrenceStatus.Failed && occ?.Exception?.Contains(uniqueException) == true;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await tracker.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("occurrence should be marked as Failed");

        var updatedOccurrence = await GetOccurrenceAsync(occurrence.Id, cts.Token);
        updatedOccurrence.Status.Should().Be(JobOccurrenceStatus.Failed);
        updatedOccurrence.Exception.Should().Contain(uniqueException);
    }

    [Fact]
    public async Task ProcessStatusUpdate_ShouldBatchProcessMultipleUpdates()
    {
        // Arrange
        await InitializeAsync();

        var job = await SeedScheduledJobAsync("BatchStatusJob");
        var uniqueWorkerId = $"batch-worker-{Guid.NewGuid():N}";

        var occurrences = new List<JobOccurrence>();
        for (int i = 0; i < 5; i++)
        {
            var occ = await SeedJobOccurrenceAsync(
                jobId: job.Id,
                jobName: job.JobNameInWorker,
                status: JobOccurrenceStatus.Queued
            );
            occurrences.Add(occ);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act - Start the tracker first
        var tracker = CreateStatusTrackerService();
        _ = Task.Run(async () =>
        {
            try
            {
                await tracker.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(1000, cts.Token);

        // Publish status updates for all occurrences
        foreach (var occ in occurrences)
        {
            await PublishStatusUpdateAsync(new JobStatusUpdateMessage
            {
                CorrelationId = occ.CorrelationId,
                JobId = job.Id,
                WorkerId = uniqueWorkerId,
                Status = JobOccurrenceStatus.Running,
                StartTime = DateTime.UtcNow
            }, cts.Token);
        }

        // Wait for all occurrences to be updated
        var found = await WaitForConditionAsync(async () =>
        {
            foreach (var occ in occurrences)
            {
                var updated = await GetOccurrenceAsync(occ.Id, cts.Token);
                if (updated?.Status != JobOccurrenceStatus.Running || updated?.WorkerId != uniqueWorkerId)
                    return false;
            }

            return true;
        },
        timeout: TimeSpan.FromSeconds(20),
        pollInterval: TimeSpan.FromMilliseconds(500),
        cancellationToken: cts.Token);

        await tracker.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("all occurrences should be updated");

        foreach (var occ in occurrences)
        {
            var updated = await GetOccurrenceAsync(occ.Id, cts.Token);
            updated.Status.Should().Be(JobOccurrenceStatus.Running);
            updated.WorkerId.Should().Be(uniqueWorkerId);
        }
    }

    [Fact]
    public async Task ProcessStatusUpdate_ShouldUpdateLastHeartbeat()
    {
        // Arrange
        await InitializeAsync();

        var job = await SeedScheduledJobAsync("HeartbeatJob");
        var occurrence = await SeedJobOccurrenceAsync(
            jobId: job.Id,
            jobName: job.JobNameInWorker,
            status: JobOccurrenceStatus.Running
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var beforeUpdate = DateTime.UtcNow;

        // Act - Start the tracker first
        var tracker = CreateStatusTrackerService();
        _ = Task.Run(async () =>
        {
            try
            {
                await tracker.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(1000, cts.Token);

        await PublishStatusUpdateAsync(new JobStatusUpdateMessage
        {
            CorrelationId = occurrence.CorrelationId,
            JobId = job.Id,
            WorkerId = "heartbeat-worker",
            Status = JobOccurrenceStatus.Running
        }, cts.Token);

        // Wait for condition
        var found = await WaitForConditionAsync(
            async () =>
            {
                var occ = await GetOccurrenceAsync(occurrence.Id, cts.Token);
                return occ?.LastHeartbeat != null && occ.LastHeartbeat > beforeUpdate.AddSeconds(-5);
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await tracker.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("heartbeat should be updated");

        var updatedOccurrence = await GetOccurrenceAsync(occurrence.Id, cts.Token);
        updatedOccurrence.LastHeartbeat.Should().NotBeNull();
        updatedOccurrence.LastHeartbeat.Should().BeAfter(DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task ProcessStatusUpdate_ShouldClearExceptionOnSuccess()
    {
        // Arrange
        await InitializeAsync();

        var job = await SeedScheduledJobAsync("ClearExceptionJob");

        // First seed with a failed status
        var dbContext = GetDbContext();
        var correlationId = Guid.CreateVersion7();
        var occurrence = new JobOccurrence
        {
            Id = correlationId,
            CorrelationId = correlationId,
            JobId = job.Id,
            JobName = job.JobNameInWorker,
            JobVersion = 1,
            Status = JobOccurrenceStatus.Running,
            Exception = "Previous error that should be cleared",
            CreatedAt = DateTime.UtcNow
        };

        await dbContext.JobOccurrences.AddAsync(occurrence);
        await dbContext.SaveChangesAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var uniqueResult = $"Success-{Guid.NewGuid():N}";

        // Act - Start the tracker first
        var tracker = CreateStatusTrackerService();
        _ = Task.Run(async () =>
        {
            try
            {
                await tracker.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(1000, cts.Token);

        await PublishStatusUpdateAsync(new JobStatusUpdateMessage
        {
            CorrelationId = occurrence.CorrelationId,
            JobId = job.Id,
            WorkerId = "test-worker",
            Status = JobOccurrenceStatus.Completed,
            EndTime = DateTime.UtcNow,
            Result = uniqueResult
        }, cts.Token);

        // Wait for condition
        var found = await WaitForConditionAsync(
            async () =>
            {
                var occ = await GetOccurrenceAsync(occurrence.Id, cts.Token);
                return occ?.Status == JobOccurrenceStatus.Completed && occ?.Result == uniqueResult;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await tracker.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("occurrence should be marked as Completed");

        var updatedOccurrence = await GetOccurrenceAsync(occurrence.Id, cts.Token);
        updatedOccurrence.Status.Should().Be(JobOccurrenceStatus.Completed);
        updatedOccurrence.Exception.Should().BeNull();
        updatedOccurrence.Result.Should().Be(uniqueResult);
    }

    private StatusTrackerService CreateStatusTrackerService() => new(
            _serviceProvider,
            _serviceProvider.GetRequiredService<IRedisSchedulerService>(),
            Options.Create(new RabbitMQOptions
            {
                Host = _factory.GetRabbitMqHost(),
                Port = _factory.GetRabbitMqPort(),
                Username = "guest",
                Password = "guest",
                VirtualHost = "/"
            }),
            Options.Create(new StatusTrackerOptions
            {
                Enabled = true,
                BatchSize = 10,
                BatchIntervalMs = 300 // Faster for tests
            }),
            Options.Create(new JobAutoDisableOptions
            {
                Enabled = true,
                ConsecutiveFailureThreshold = 5,
                FailureWindowMinutes = 60
            }),
            _serviceProvider.GetRequiredService<ILoggerFactory>()
        );

    private async Task PublishStatusUpdateAsync(JobStatusUpdateMessage message, CancellationToken cancellationToken)
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

        // Ensure queue exists
        await channel.QueueDeclareAsync(
            queue: WorkerConstant.Queues.StatusUpdates,
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
            routingKey: WorkerConstant.Queues.StatusUpdates,
            mandatory: false,
            basicProperties: properties,
            body: body,
            cancellationToken: cancellationToken);
    }
}
