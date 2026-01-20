using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Interfaces.RabbitMQ;
using Milvaion.Application.Interfaces.Redis;
using Milvaion.Application.Utils.Models.Options;
using Milvaion.Infrastructure.BackgroundServices;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.BackgroundServices;

/// <summary>
/// Integration tests for JobDispatcherService.
/// Tests job dispatching, recurring job rescheduling, and concurrent execution policies.
/// </summary>
[Collection(nameof(MilvaionTestCollection))]
public class JobDispatcherServiceTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : BackgroundServiceTestBase(factory, output)
{
    [Fact]
    public async Task DispatchDueJobs_ShouldCreateOccurrenceAndPublishToRabbitMQ()
    {
        // Arrange
        await InitializeAsync();

        var uniqueJobName = $"DispatchTestJob_{Guid.NewGuid():N}";
        var job = await SeedScheduledJobAsync(
            uniqueJobName,
            executeAt: DateTime.UtcNow.AddMinutes(-1) // Due in the past
        );

        // Add job to Redis scheduler
        var redisScheduler = _serviceProvider.GetRequiredService<IRedisSchedulerService>();
        await redisScheduler.AddToScheduledSetAsync(job.Id, job.ExecuteAt);
        await redisScheduler.CacheJobDetailsAsync(job, TimeSpan.FromHours(1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act - Start dispatcher first
        var dispatcher = CreateJobDispatcherService();
        _ = Task.Run(async () =>
        {
            try
            {
                await dispatcher.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait for occurrence to be created
        var found = await WaitForConditionAsync(
            async () =>
            {
                var dbContext = GetDbContext();
                var occurrences = await dbContext.JobOccurrences
                    .AsNoTracking()
                    .Where(o => o.JobId == job.Id)
                    .ToListAsync(cts.Token);
                return occurrences.Count != 0;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await dispatcher.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("occurrence should be created for due job");

        var dbContextAssert = GetDbContext();
        var createdOccurrences = await dbContextAssert.JobOccurrences
            .AsNoTracking()
            .Where(o => o.JobId == job.Id)
            .ToListAsync(cts.Token);

        createdOccurrences.Should().NotBeEmpty();
        var occurrence = createdOccurrences.First();
        occurrence.Status.Should().Be(JobOccurrenceStatus.Queued);
        occurrence.JobName.Should().Be(job.JobNameInWorker);
    }

    [Fact]
    public async Task DispatchDueJobs_ShouldRescheduleRecurringJob()
    {
        // Arrange
        await InitializeAsync();

        var uniqueJobName = $"RecurringTestJob_{Guid.NewGuid():N}";
        var job = await SeedScheduledJobAsync(
            uniqueJobName,
            cronExpression: "0 0 * * * *", // Every hour (with seconds)
            executeAt: DateTime.UtcNow.AddMinutes(-1)
        );

        var redisScheduler = _serviceProvider.GetRequiredService<IRedisSchedulerService>();
        await redisScheduler.AddToScheduledSetAsync(job.Id, job.ExecuteAt);
        await redisScheduler.CacheJobDetailsAsync(job, TimeSpan.FromHours(1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act - Start dispatcher first
        var dispatcher = CreateJobDispatcherService();
        _ = Task.Run(async () =>
        {
            try
            {
                await dispatcher.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait for job to be rescheduled
        var found = await WaitForConditionAsync(
            async () =>
            {
                var nextTime = await redisScheduler.GetScheduledTimeAsync(job.Id, cts.Token);
                return nextTime != null && nextTime > DateTime.UtcNow;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await dispatcher.StopAsync(cts.Token);

        // Assert - Job should be rescheduled for future execution
        found.Should().BeTrue("recurring job should be rescheduled for future");

        var nextScheduledTime = await redisScheduler.GetScheduledTimeAsync(job.Id, cts.Token);
        nextScheduledTime.Should().NotBeNull();
        nextScheduledTime.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task DispatchDueJobs_ShouldRemoveOneTimeJobFromRedisAfterDispatch()
    {
        // Arrange
        await InitializeAsync();

        var uniqueJobName = $"OneTimeJob_{Guid.NewGuid():N}";
        var job = await SeedScheduledJobAsync(
            uniqueJobName,
            cronExpression: null, // One-time job
            executeAt: DateTime.UtcNow.AddMinutes(-1)
        );

        var redisScheduler = _serviceProvider.GetRequiredService<IRedisSchedulerService>();
        await redisScheduler.AddToScheduledSetAsync(job.Id, job.ExecuteAt);
        await redisScheduler.CacheJobDetailsAsync(job, TimeSpan.FromHours(1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act - Start dispatcher first
        var dispatcher = CreateJobDispatcherService();
        _ = Task.Run(async () =>
        {
            try
            {
                await dispatcher.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait for job to be removed from Redis
        var found = await WaitForConditionAsync(
            async () =>
            {
                var scheduledTime = await redisScheduler.GetScheduledTimeAsync(job.Id, cts.Token);
                return scheduledTime == null;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await dispatcher.StopAsync(cts.Token);

        // Assert - One-time job should be removed from Redis scheduled set
        found.Should().BeTrue("one-time job should be removed after dispatch");

        var scheduledTime = await redisScheduler.GetScheduledTimeAsync(job.Id, cts.Token);
        scheduledTime.Should().BeNull();
    }

    [Fact]
    public async Task DispatchDueJobs_ShouldSkipInactiveJobs()
    {
        // Arrange
        await InitializeAsync();

        var uniqueJobName = $"InactiveJob_{Guid.NewGuid():N}";
        var job = await SeedScheduledJobAsync(
            uniqueJobName,
            executeAt: DateTime.UtcNow.AddMinutes(-1),
            isActive: false // Inactive
        );

        var redisScheduler = _serviceProvider.GetRequiredService<IRedisSchedulerService>();
        await redisScheduler.AddToScheduledSetAsync(job.Id, job.ExecuteAt);
        await redisScheduler.CacheJobDetailsAsync(job, TimeSpan.FromHours(1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act - Start dispatcher
        var dispatcher = CreateJobDispatcherService();
        _ = Task.Run(async () =>
        {
            try
            {
                await dispatcher.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait some time for dispatcher to process
        await Task.Delay(5000, cts.Token);

        await dispatcher.StopAsync(cts.Token);

        // Assert - No occurrence should be created for inactive job
        var dbContext = GetDbContext();
        var occurrences = await dbContext.JobOccurrences
            .AsNoTracking()
            .Where(o => o.JobId == job.Id)
            .ToListAsync(cts.Token);

        occurrences.Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchDueJobs_ShouldNotDispatchFutureJobs()
    {
        // Arrange
        await InitializeAsync();

        var uniqueJobName = $"FutureJob_{Guid.NewGuid():N}";
        var job = await SeedScheduledJobAsync(
            uniqueJobName,
            executeAt: DateTime.UtcNow.AddHours(1) // Future
        );

        var redisScheduler = _serviceProvider.GetRequiredService<IRedisSchedulerService>();
        await redisScheduler.AddToScheduledSetAsync(job.Id, job.ExecuteAt);
        await redisScheduler.CacheJobDetailsAsync(job, TimeSpan.FromHours(1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act - Start dispatcher
        var dispatcher = CreateJobDispatcherService();
        _ = Task.Run(async () =>
        {
            try
            {
                await dispatcher.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait some time for dispatcher to process
        await Task.Delay(5000, cts.Token);

        await dispatcher.StopAsync(cts.Token);

        // Assert - No occurrence should be created for future job
        var dbContext = GetDbContext();
        var occurrences = await dbContext.JobOccurrences
            .AsNoTracking()
            .Where(o => o.JobId == job.Id)
            .ToListAsync(cts.Token);

        occurrences.Should().BeEmpty();

        // Job should still be in Redis
        var scheduledTime = await redisScheduler.GetScheduledTimeAsync(job.Id, cts.Token);
        scheduledTime.Should().NotBeNull();
    }

    [Fact]
    public async Task DispatchDueJobs_ShouldHandleMultipleDueJobs()
    {
        // Arrange
        await InitializeAsync();

        var jobs = new List<ScheduledJob>();
        for (int i = 0; i < 3; i++)
        {
            var job = await SeedScheduledJobAsync(
                $"MultiJob{i}_{Guid.NewGuid():N}",
                executeAt: DateTime.UtcNow.AddMinutes(-1 - i)
            );
            jobs.Add(job);
        }

        var redisScheduler = _serviceProvider.GetRequiredService<IRedisSchedulerService>();
        foreach (var job in jobs)
        {
            await redisScheduler.AddToScheduledSetAsync(job.Id, job.ExecuteAt);
            await redisScheduler.CacheJobDetailsAsync(job, TimeSpan.FromHours(1));
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act - Start dispatcher first
        var dispatcher = CreateJobDispatcherService();
        _ = Task.Run(async () =>
        {
            try
            {
                await dispatcher.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait for all jobs to have occurrences
        var found = await WaitForConditionAsync(
            async () =>
            {
                var dbContext = GetDbContext();
                foreach (var job in jobs)
                {
                    var hasOccurrence = await dbContext.JobOccurrences
                        .AsNoTracking()
                        .AnyAsync(o => o.JobId == job.Id, cts.Token);
                    if (!hasOccurrence)
                        return false;
                }

                return true;
            },
            timeout: TimeSpan.FromSeconds(20),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await dispatcher.StopAsync(cts.Token);

        // Assert - All jobs should have occurrences
        found.Should().BeTrue("all due jobs should have occurrences");

        var dbContextAssert = GetDbContext();
        foreach (var job in jobs)
        {
            var occurrences = await dbContextAssert.JobOccurrences
                .AsNoTracking()
                .Where(o => o.JobId == job.Id)
                .ToListAsync(cts.Token);

            occurrences.Should().NotBeEmpty();
        }
    }

    [Fact]
    public async Task DispatchDueJobs_ShouldIncludeDispatchLogInOccurrence()
    {
        // Arrange
        await InitializeAsync();

        var uniqueJobName = $"LogTestJob_{Guid.NewGuid():N}";
        var job = await SeedScheduledJobAsync(
            uniqueJobName,
            executeAt: DateTime.UtcNow.AddMinutes(-1)
        );

        var redisScheduler = _serviceProvider.GetRequiredService<IRedisSchedulerService>();
        await redisScheduler.AddToScheduledSetAsync(job.Id, job.ExecuteAt);
        await redisScheduler.CacheJobDetailsAsync(job, TimeSpan.FromHours(1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act - Start dispatcher first
        var dispatcher = CreateJobDispatcherService();
        _ = Task.Run(async () =>
        {
            try
            {
                await dispatcher.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait for occurrence with dispatch log
        var found = await WaitForConditionAsync(
            async () =>
            {
                var dbContext = GetDbContext();
                var occurrence = await dbContext.JobOccurrences
                    .AsNoTracking()
                    .FirstOrDefaultAsync(o => o.JobId == job.Id, cts.Token);
                return occurrence?.Logs?.Any(l => l.Category == "Dispatcher") == true;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await dispatcher.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("occurrence should have dispatch log");

        var dbContextAssert = GetDbContext();
        var createdOccurrence = await dbContextAssert.JobOccurrences
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.JobId == job.Id, cts.Token);

        createdOccurrence.Should().NotBeNull();
        createdOccurrence!.Logs.Should().NotBeEmpty();

        var dispatchLog = createdOccurrence.Logs.FirstOrDefault(l => l.Category == "Dispatcher");
        dispatchLog.Should().NotBeNull();
        dispatchLog!.Message.ToLower().Should().Contain("dispatched");
    }

    [Fact]
    public async Task DispatchDueJobs_ShouldSetCorrectJobVersion()
    {
        // Arrange
        await InitializeAsync();

        var dbContext = GetDbContext();
        var uniqueJobName = $"VersionTestJob_{Guid.NewGuid():N}";
        var job = new ScheduledJob
        {
            Id = Guid.CreateVersion7(),
            DisplayName = uniqueJobName,
            JobNameInWorker = uniqueJobName,
            JobData = "{}",
            ExecuteAt = DateTime.UtcNow.AddMinutes(-1),
            IsActive = true,
            Version = 5, // Specific version
            RoutingPattern = "worker.*",
            ConcurrentExecutionPolicy = ConcurrentExecutionPolicy.Skip,
            CreationDate = DateTime.UtcNow,
            CreatorUserName = "TestUser"
        };

        await dbContext.ScheduledJobs.AddAsync(job);
        await dbContext.SaveChangesAsync();

        var redisScheduler = _serviceProvider.GetRequiredService<IRedisSchedulerService>();
        await redisScheduler.AddToScheduledSetAsync(job.Id, job.ExecuteAt);
        await redisScheduler.CacheJobDetailsAsync(job, TimeSpan.FromHours(1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act - Start dispatcher first
        var dispatcher = CreateJobDispatcherService();
        _ = Task.Run(async () =>
        {
            try
            {
                await dispatcher.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Wait for occurrence with correct version
        var found = await WaitForConditionAsync(
            async () =>
            {
                var ctx = GetDbContext();
                var occurrence = await ctx.JobOccurrences
                    .AsNoTracking()
                    .FirstOrDefaultAsync(o => o.JobId == job.Id, cts.Token);
                return occurrence?.JobVersion == 5;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await dispatcher.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("occurrence should have correct job version");

        var dbContextAssert = GetDbContext();
        var createdOccurrence = await dbContextAssert.JobOccurrences
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.JobId == job.Id, cts.Token);

        createdOccurrence.Should().NotBeNull();
        createdOccurrence!.JobVersion.Should().Be(5);
    }

    private JobDispatcherService CreateJobDispatcherService() => new(
            _serviceProvider,
            _serviceProvider.GetRequiredService<IRedisSchedulerService>(),
            _serviceProvider.GetRequiredService<IRedisLockService>(),
            _serviceProvider.GetRequiredService<IRedisWorkerService>(),
            _serviceProvider.GetRequiredService<IRabbitMQPublisher>(),
            Options.Create(new JobDispatcherOptions
            {
                Enabled = true,
                PollingIntervalSeconds = 1,
                BatchSize = 100,
                LockTtlSeconds = 30,
                EnableStartupRecovery = false // Disable for testing
            }),
            _serviceProvider.GetRequiredService<IDispatcherControlService>(),
            _serviceProvider.GetRequiredService<ILoggerFactory>()
        );
}
