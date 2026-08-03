using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces.Redis;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.Models.Options;
using Milvaion.Infrastructure.BackgroundServices;
using Milvaion.Infrastructure.Services.RabbitMQ;
using Milvaion.Infrastructure.Telemetry;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Domain.JsonModels;
using Milvasoft.Milvaion.Sdk.Models;
using Milvasoft.Milvaion.Sdk.Utils;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.BackgroundServices;

/// <summary>
/// Integration tests for ExternalJobTrackerService.
/// Tests external job registration and occurrence message consumption from RabbitMQ.
/// </summary>
[Collection(nameof(ServicesTestCollection))]
public class ExternalJobTrackerServiceTests(ServicesWebApplicationFactory factory, ITestOutputHelper output) : BackgroundServiceTestBase(factory, output)
{
    [Fact]
    public async Task ProcessRegistration_ShouldCreateNewExternalJob()
    {
        // Arrange
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var uniqueExternalJobId = $"DEFAULT.TestJob_{Guid.CreateVersion7():N}";
        var uniqueDisplayName = $"External Test Job {Guid.CreateVersion7():N}";

        await SeedWorkerInRedisAsync("test-worker", uniqueExternalJobId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act - Start the tracker first
        var tracker = CreateExternalJobTrackerService();
        _ = Task.Run(async () =>
        {
            try
            {
                await tracker.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(3000, cts.Token);

        // Publish registration message
        await PublishRegistrationMessageAsync(new ExternalJobRegistrationMessage
        {
            ExternalJobId = uniqueExternalJobId,
            DisplayName = uniqueDisplayName,
            Description = "Integration test external job",
            JobTypeName = "TestApp.Jobs.TestJob",
            CronExpression = "0 */5 * * * *",
            IsActive = true,
            Source = "Quartz",
            WorkerId = "test-worker",
            Tags = "integration-test"
        }, cts.Token);

        // Wait for job to be created
        var found = await WaitForConditionAsync(
            async () =>
            {
                var dbContext = GetDbContext();
                return await dbContext.ScheduledJobs
                    .AsNoTracking()
                    .AnyAsync(j => j.IsExternal && j.ExternalJobId == uniqueExternalJobId, cts.Token);
            },
            timeout: TimeSpan.FromSeconds(20),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await tracker.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("external job should be created from registration message");

        var dbContextAssert = GetDbContext();
        var createdJob = await dbContextAssert.ScheduledJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.IsExternal && j.ExternalJobId == uniqueExternalJobId, cts.Token);

        createdJob.Should().NotBeNull();
        createdJob!.DisplayName.Should().Be(uniqueDisplayName);
        createdJob.IsExternal.Should().BeTrue();
        createdJob.ExternalJobId.Should().Be(uniqueExternalJobId);
        createdJob.CronExpression.Should().Be("0 */5 * * * *");
        createdJob.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task ProcessRegistration_ShouldUpdateExistingExternalJob()
    {
        // Arrange
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var uniqueExternalJobId = $"DEFAULT.UpdateJob_{Guid.CreateVersion7():N}";

        await SeedWorkerInRedisAsync("test-worker", uniqueExternalJobId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var tracker = CreateExternalJobTrackerService();
        _ = Task.Run(async () =>
        {
            try
            {
                await tracker.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(3000, cts.Token);

        // Create initial job
        await PublishRegistrationMessageAsync(new ExternalJobRegistrationMessage
        {
            ExternalJobId = uniqueExternalJobId,
            DisplayName = "Original Name",
            Description = "Original description",
            JobTypeName = "TestApp.Jobs.UpdateJob",
            IsActive = true,
            Source = "Quartz",
            WorkerId = "test-worker"
        }, cts.Token);

        // Wait for job to be created
        var created = await WaitForConditionAsync(
            async () =>
            {
                var dbContext = GetDbContext();
                return await dbContext.ScheduledJobs
                    .AsNoTracking()
                    .AnyAsync(j => j.IsExternal && j.ExternalJobId == uniqueExternalJobId, cts.Token);
            },
            timeout: TimeSpan.FromSeconds(20),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        created.Should().BeTrue("initial job should be created before update");

        // Act - Publish update registration
        var updatedDisplayName = $"Updated Name {Guid.CreateVersion7():N}";
        await PublishRegistrationMessageAsync(new ExternalJobRegistrationMessage
        {
            ExternalJobId = uniqueExternalJobId,
            DisplayName = updatedDisplayName,
            Description = "Updated description",
            JobTypeName = "TestApp.Jobs.UpdateJob",
            CronExpression = "0 0 * * * *",
            IsActive = true,
            Source = "Quartz",
            WorkerId = "test-worker"
        }, cts.Token);

        // Wait for job to be updated
        var found = await WaitForConditionAsync(
            async () =>
            {
                var dbContext = GetDbContext();
                var job = await dbContext.ScheduledJobs
                    .AsNoTracking()
                    .FirstOrDefaultAsync(j => j.IsExternal && j.ExternalJobId == uniqueExternalJobId, cts.Token);
                return job?.DisplayName == updatedDisplayName;
            },
            timeout: TimeSpan.FromSeconds(20),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await tracker.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("external job should be updated from registration message");

        var dbContextAssert = GetDbContext();
        var updatedJob = await dbContextAssert.ScheduledJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.IsExternal && j.ExternalJobId == uniqueExternalJobId, cts.Token);

        updatedJob.Should().NotBeNull();
        updatedJob!.DisplayName.Should().Be(updatedDisplayName);
    }

    [Fact]
    public async Task ProcessOccurrence_Starting_ShouldCreateOccurrence()
    {
        // Arrange
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var uniqueExternalJobId = $"DEFAULT.OccStartJob_{Guid.CreateVersion7():N}";

        await SeedWorkerInRedisAsync("test-worker", uniqueExternalJobId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var tracker = CreateExternalJobTrackerService();
        _ = Task.Run(async () =>
        {
            try
            {
                await tracker.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(3000, cts.Token);

        // First register the job so the ExternalJobId -> JobId mapping exists
        await PublishRegistrationMessageAsync(new ExternalJobRegistrationMessage
        {
            ExternalJobId = uniqueExternalJobId,
            DisplayName = "Occurrence Start Test Job",
            JobTypeName = "TestApp.Jobs.OccStartJob",
            IsActive = true,
            Source = "Quartz",
            WorkerId = "test-worker"
        }, cts.Token);

        // Wait for job to be created
        var jobCreated = await WaitForConditionAsync(
            async () =>
            {
                var dbContext = GetDbContext();
                return await dbContext.ScheduledJobs
                    .AsNoTracking()
                    .AnyAsync(j => j.IsExternal && j.ExternalJobId == uniqueExternalJobId, cts.Token);
            },
            timeout: TimeSpan.FromSeconds(20),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        jobCreated.Should().BeTrue("job must exist before occurrence can be created");

        // Act - Publish Starting occurrence
        var correlationId = Guid.CreateVersion7();
        var startTime = DateTime.UtcNow;

        await PublishOccurrenceMessageAsync(new ExternalJobOccurrenceMessage
        {
            ExternalJobId = uniqueExternalJobId,
            ExternalOccurrenceId = $"fire-{Guid.CreateVersion7():N}",
            CorrelationId = correlationId,
            JobTypeName = "TestApp.Jobs.OccStartJob",
            WorkerId = "test-worker",
            EventType = ExternalOccurrenceEventType.Starting,
            Status = JobOccurrenceStatus.Running,
            StartTime = startTime,
            ActualFireTime = startTime,
            Source = "Quartz"
        }, cts.Token);

        // Wait for occurrence to be created
        var found = await WaitForConditionAsync(
            async () =>
            {
                var dbContext = GetDbContext();
                return await dbContext.JobOccurrences
                    .AsNoTracking()
                    .AnyAsync(o => o.CorrelationId == correlationId, cts.Token);
            },
            timeout: TimeSpan.FromSeconds(20),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await tracker.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("occurrence should be created from Starting event");

        var dbContextAssert = GetDbContext();
        var occurrence = await dbContextAssert.JobOccurrences
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.CorrelationId == correlationId, cts.Token);

        occurrence.Should().NotBeNull();
        occurrence!.Status.Should().Be(JobOccurrenceStatus.Running);
        occurrence.WorkerId.Should().Be("test-worker");
        occurrence.StartTime.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessOccurrence_Completed_ShouldUpdateOccurrence()
    {
        // Arrange
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var uniqueExternalJobId = $"DEFAULT.OccCompleteJob_{Guid.CreateVersion7():N}";

        await SeedWorkerInRedisAsync("test-worker", uniqueExternalJobId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var tracker = CreateExternalJobTrackerService();
        _ = Task.Run(async () =>
        {
            try
            {
                await tracker.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(3000, cts.Token);

        // Register job first
        await PublishRegistrationMessageAsync(new ExternalJobRegistrationMessage
        {
            ExternalJobId = uniqueExternalJobId,
            DisplayName = "Completion Test Job",
            JobTypeName = "TestApp.Jobs.CompleteJob",
            IsActive = true,
            Source = "Quartz",
            WorkerId = "test-worker"
        }, cts.Token);

        var jobCreated = await WaitForConditionAsync(
            async () =>
            {
                var dbContext = GetDbContext();
                return await dbContext.ScheduledJobs
                    .AsNoTracking()
                    .AnyAsync(j => j.IsExternal && j.ExternalJobId == uniqueExternalJobId, cts.Token);
            },
            timeout: TimeSpan.FromSeconds(20),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        jobCreated.Should().BeTrue();

        // Create Starting occurrence first
        var correlationId = Guid.CreateVersion7();
        var startTime = DateTime.UtcNow;

        await PublishOccurrenceMessageAsync(new ExternalJobOccurrenceMessage
        {
            ExternalJobId = uniqueExternalJobId,
            ExternalOccurrenceId = $"fire-{Guid.CreateVersion7():N}",
            CorrelationId = correlationId,
            JobTypeName = "TestApp.Jobs.CompleteJob",
            WorkerId = "test-worker",
            EventType = ExternalOccurrenceEventType.Starting,
            Status = JobOccurrenceStatus.Running,
            StartTime = startTime,
            Source = "Quartz"
        }, cts.Token);

        var occurrenceCreated = await WaitForConditionAsync(
            async () =>
            {
                var dbContext = GetDbContext();
                return await dbContext.JobOccurrences
                    .AsNoTracking()
                    .AnyAsync(o => o.CorrelationId == correlationId, cts.Token);
            },
            timeout: TimeSpan.FromSeconds(20),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        occurrenceCreated.Should().BeTrue("occurrence must be created before completion");

        // Act - Publish Completed event
        var uniqueResult = $"Job completed - {Guid.CreateVersion7():N}";

        await PublishOccurrenceMessageAsync(new ExternalJobOccurrenceMessage
        {
            ExternalJobId = uniqueExternalJobId,
            ExternalOccurrenceId = $"fire-{Guid.CreateVersion7():N}",
            CorrelationId = correlationId,
            JobTypeName = "TestApp.Jobs.CompleteJob",
            WorkerId = "test-worker",
            EventType = ExternalOccurrenceEventType.Completed,
            Status = JobOccurrenceStatus.Completed,
            EndTime = DateTime.UtcNow,
            DurationMs = 5000,
            Result = uniqueResult,
            Source = "Quartz"
        }, cts.Token);

        // Wait for occurrence to be updated
        var found = await WaitForConditionAsync(
            async () =>
            {
                var dbContext = GetDbContext();
                var occ = await dbContext.JobOccurrences
                    .AsNoTracking()
                    .FirstOrDefaultAsync(o => o.CorrelationId == correlationId, cts.Token);
                return occ?.Status == JobOccurrenceStatus.Completed && occ?.Result == uniqueResult;
            },
            timeout: TimeSpan.FromSeconds(20),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await tracker.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("occurrence should be updated to Completed");

        var dbContextAssert = GetDbContext();
        var occurrence = await dbContextAssert.JobOccurrences
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.CorrelationId == correlationId, cts.Token);

        occurrence.Should().NotBeNull();
        occurrence!.Status.Should().Be(JobOccurrenceStatus.Completed);
        occurrence.DurationMs.Should().Be(5000);
        occurrence.Result.Should().Be(uniqueResult);
        occurrence.EndTime.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessOccurrence_Failed_ShouldRecordException()
    {
        // Arrange
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var uniqueExternalJobId = $"DEFAULT.OccFailJob_{Guid.CreateVersion7():N}";

        await SeedWorkerInRedisAsync("test-worker", uniqueExternalJobId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var tracker = CreateExternalJobTrackerService();
        _ = Task.Run(async () =>
        {
            try
            {
                await tracker.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(3000, cts.Token);

        // Register job
        await PublishRegistrationMessageAsync(new ExternalJobRegistrationMessage
        {
            ExternalJobId = uniqueExternalJobId,
            DisplayName = "Failure Test Job",
            JobTypeName = "TestApp.Jobs.FailJob",
            IsActive = true,
            Source = "Hangfire",
            WorkerId = "test-worker"
        }, cts.Token);

        var jobCreated = await WaitForConditionAsync(
            async () =>
            {
                var dbContext = GetDbContext();
                return await dbContext.ScheduledJobs
                    .AsNoTracking()
                    .AnyAsync(j => j.IsExternal && j.ExternalJobId == uniqueExternalJobId, cts.Token);
            },
            timeout: TimeSpan.FromSeconds(20),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        jobCreated.Should().BeTrue();

        // Create Starting occurrence
        var correlationId = Guid.CreateVersion7();

        await PublishOccurrenceMessageAsync(new ExternalJobOccurrenceMessage
        {
            ExternalJobId = uniqueExternalJobId,
            ExternalOccurrenceId = $"fire-{Guid.CreateVersion7():N}",
            CorrelationId = correlationId,
            JobTypeName = "TestApp.Jobs.FailJob",
            WorkerId = "test-worker",
            EventType = ExternalOccurrenceEventType.Starting,
            Status = JobOccurrenceStatus.Running,
            StartTime = DateTime.UtcNow,
            Source = "Hangfire"
        }, cts.Token);

        var occurrenceCreated = await WaitForConditionAsync(
            async () =>
            {
                var dbContext = GetDbContext();
                return await dbContext.JobOccurrences
                    .AsNoTracking()
                    .AnyAsync(o => o.CorrelationId == correlationId, cts.Token);
            },
            timeout: TimeSpan.FromSeconds(20),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        occurrenceCreated.Should().BeTrue();

        // Act - Publish Failed event
        var uniqueException = $"NullReferenceException: Object ref {Guid.CreateVersion7():N}";

        await PublishOccurrenceMessageAsync(new ExternalJobOccurrenceMessage
        {
            ExternalJobId = uniqueExternalJobId,
            ExternalOccurrenceId = $"fire-{Guid.CreateVersion7():N}",
            CorrelationId = correlationId,
            JobTypeName = "TestApp.Jobs.FailJob",
            WorkerId = "test-worker",
            EventType = ExternalOccurrenceEventType.Completed,
            Status = JobOccurrenceStatus.Failed,
            EndTime = DateTime.UtcNow,
            DurationMs = 1500,
            Exception = uniqueException,
            Source = "Hangfire"
        }, cts.Token);

        // Wait for occurrence to be updated
        var found = await WaitForConditionAsync(
            async () =>
            {
                var dbContext = GetDbContext();
                var occ = await dbContext.JobOccurrences
                    .AsNoTracking()
                    .FirstOrDefaultAsync(o => o.CorrelationId == correlationId, cts.Token);
                return occ?.Status == JobOccurrenceStatus.Failed && occ?.Exception?.Contains(uniqueException) == true;
            },
            timeout: TimeSpan.FromSeconds(20),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await tracker.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("occurrence should be updated to Failed with exception");

        var dbContextAssert = GetDbContext();
        var occurrence = await dbContextAssert.JobOccurrences
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.CorrelationId == correlationId, cts.Token);

        occurrence.Should().NotBeNull();
        occurrence!.Status.Should().Be(JobOccurrenceStatus.Failed);
        occurrence.Exception.Should().Contain(uniqueException);
        occurrence.DurationMs.Should().Be(1500);
        occurrence.EndTime.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessOccurrence_ShouldTrackStatusChangeHistory()
    {
        // Arrange
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var uniqueExternalJobId = $"DEFAULT.StatusHistoryJob_{Guid.CreateVersion7():N}";

        await SeedWorkerInRedisAsync("test-worker", uniqueExternalJobId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var tracker = CreateExternalJobTrackerService();
        _ = Task.Run(async () =>
        {
            try
            {
                await tracker.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(3000, cts.Token);

        // Register job
        await PublishRegistrationMessageAsync(new ExternalJobRegistrationMessage
        {
            ExternalJobId = uniqueExternalJobId,
            DisplayName = "Status History Test Job",
            JobTypeName = "TestApp.Jobs.StatusHistoryJob",
            IsActive = true,
            Source = "Quartz",
            WorkerId = "test-worker"
        }, cts.Token);

        var jobCreated = await WaitForConditionAsync(
            async () =>
            {
                var dbContext = GetDbContext();
                return await dbContext.ScheduledJobs
                    .AsNoTracking()
                    .AnyAsync(j => j.IsExternal && j.ExternalJobId == uniqueExternalJobId, cts.Token);
            },
            timeout: TimeSpan.FromSeconds(20),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        jobCreated.Should().BeTrue();

        // Create occurrence (Starting -> Running)
        var correlationId = Guid.CreateVersion7();

        await PublishOccurrenceMessageAsync(new ExternalJobOccurrenceMessage
        {
            ExternalJobId = uniqueExternalJobId,
            ExternalOccurrenceId = $"fire-{Guid.CreateVersion7():N}",
            CorrelationId = correlationId,
            JobTypeName = "TestApp.Jobs.StatusHistoryJob",
            WorkerId = "test-worker",
            EventType = ExternalOccurrenceEventType.Starting,
            Status = JobOccurrenceStatus.Running,
            StartTime = DateTime.UtcNow,
            Source = "Quartz"
        }, cts.Token);

        var occurrenceCreated = await WaitForConditionAsync(
            async () =>
            {
                var dbContext = GetDbContext();
                return await dbContext.JobOccurrences
                    .AsNoTracking()
                    .AnyAsync(o => o.CorrelationId == correlationId, cts.Token);
            },
            timeout: TimeSpan.FromSeconds(20),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        occurrenceCreated.Should().BeTrue();

        // Act - Complete the occurrence (Running -> Completed)
        await PublishOccurrenceMessageAsync(new ExternalJobOccurrenceMessage
        {
            ExternalJobId = uniqueExternalJobId,
            ExternalOccurrenceId = $"fire-{Guid.CreateVersion7():N}",
            CorrelationId = correlationId,
            JobTypeName = "TestApp.Jobs.StatusHistoryJob",
            WorkerId = "test-worker",
            EventType = ExternalOccurrenceEventType.Completed,
            Status = JobOccurrenceStatus.Completed,
            EndTime = DateTime.UtcNow,
            DurationMs = 3000,
            Source = "Quartz"
        }, cts.Token);

        // Wait for completion
        var found = await WaitForConditionAsync(
            async () =>
            {
                var dbContext = GetDbContext();
                var occ = await dbContext.JobOccurrences
                    .AsNoTracking()
                    .FirstOrDefaultAsync(o => o.CorrelationId == correlationId, cts.Token);
                return occ?.Status == JobOccurrenceStatus.Completed;
            },
            timeout: TimeSpan.FromSeconds(20),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await tracker.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("occurrence should complete and have status change logs");

        var dbContextAssert = GetDbContext();
        var occurrence = await dbContextAssert.JobOccurrences
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.CorrelationId == correlationId, cts.Token);

        occurrence.Should().NotBeNull();
        occurrence!.StatusChangeLogs.Should().NotBeNull();
        occurrence.StatusChangeLogs.Should().HaveCountGreaterOrEqualTo(2);
        occurrence.StatusChangeLogs.Should().Contain(s => s.From == JobOccurrenceStatus.Queued && s.To == JobOccurrenceStatus.Running);
        occurrence.StatusChangeLogs.Should().Contain(s => s.From == JobOccurrenceStatus.Running && s.To == JobOccurrenceStatus.Completed);
    }

    [Fact]
    public async Task ProcessRegistration_ShouldSetExternalJobTags()
    {
        // Arrange
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var uniqueExternalJobId = $"DEFAULT.TagsJob_{Guid.CreateVersion7():N}";

        await SeedWorkerInRedisAsync("test-worker", uniqueExternalJobId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var tracker = CreateExternalJobTrackerService();
        _ = Task.Run(async () =>
        {
            try
            {
                await tracker.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(3000, cts.Token);

        // Act
        await PublishRegistrationMessageAsync(new ExternalJobRegistrationMessage
        {
            ExternalJobId = uniqueExternalJobId,
            DisplayName = "Tags Test Job",
            JobTypeName = "TestApp.Jobs.TagsJob",
            IsActive = true,
            Source = "Hangfire",
            WorkerId = "test-worker",
            Tags = "email,notifications"
        }, cts.Token);

        var found = await WaitForConditionAsync(
            async () =>
            {
                var dbContext = GetDbContext();
                return await dbContext.ScheduledJobs
                    .AsNoTracking()
                    .AnyAsync(j => j.IsExternal && j.ExternalJobId == uniqueExternalJobId, cts.Token);
            },
            timeout: TimeSpan.FromSeconds(20),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await tracker.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("external job with tags should be created");

        var dbContextAssert = GetDbContext();
        var createdJob = await dbContextAssert.ScheduledJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.IsExternal && j.ExternalJobId == uniqueExternalJobId, cts.Token);

        createdJob.Should().NotBeNull();
        createdJob!.Tags.Should().Contain("source:Hangfire");
        createdJob.Tags.Should().Contain("email,notifications");
    }

    private ExternalJobTrackerService CreateExternalJobTrackerService() => new(
            _serviceProvider,
            _serviceProvider.GetRequiredService<RabbitMQConnectionFactory>(),
            Options.Create(new ExternalJobTrackerOptions
            {
                Enabled = true,
                RegistrationBatchSize = 1,
                OccurrenceBatchSize = 1,
                BatchIntervalMs = 200
            }),
            _serviceProvider.GetRequiredService<IOptions<RabbitMQOptions>>(),
            _serviceProvider.GetRequiredService<IRedisSchedulerService>(),
            _serviceProvider.GetRequiredService<IRedisStatsService>(),
            _serviceProvider.GetRequiredService<ILoggerFactory>(),
            _serviceProvider.GetRequiredService<BackgroundServiceMetrics>()
        );

    private async Task PublishRegistrationMessageAsync(ExternalJobRegistrationMessage message, CancellationToken cancellationToken)
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
            queue: WorkerConstant.Queues.ExternalJobRegistration,
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
            routingKey: WorkerConstant.Queues.ExternalJobRegistration,
            mandatory: false,
            basicProperties: properties,
            body: body,
            cancellationToken: cancellationToken);
    }

    private async Task SeedWorkerInRedisAsync(string workerId, string externalJobId)
    {
        var workerService = _serviceProvider.GetRequiredService<IRedisWorkerService>();

        var registration = new WorkerDiscoveryRequest
        {
            WorkerId = workerId,
            InstanceId = $"{workerId}-test-instance",
            DisplayName = $"Test Worker {workerId}",
            HostName = "test-host",
            IpAddress = "127.0.0.1",
            JobTypes = [externalJobId],
            RoutingPatterns = new Dictionary<string, string> { [externalJobId] = $"worker.{workerId}" },
            MaxParallelJobs = 10,
            Version = "1.0.0",
            Metadata = JsonSerializer.Serialize(new { IsExternal = true, ExternalScheduler = "Test" })
        };

        var result = await workerService.RegisterWorkerAsync(registration);

        result.Should().BeTrue($"worker '{workerId}' should be registered in Redis before test");
    }

    private async Task PublishOccurrenceMessageAsync(ExternalJobOccurrenceMessage message, CancellationToken cancellationToken)
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
            queue: WorkerConstant.Queues.ExternalJobOccurrence,
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
            routingKey: WorkerConstant.Queues.ExternalJobOccurrence,
            mandatory: false,
            basicProperties: properties,
            body: body,
            cancellationToken: cancellationToken);
    }
}
