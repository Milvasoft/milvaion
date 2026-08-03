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
using Milvaion.Infrastructure.Telemetry;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using System.Text.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.BackgroundServices;

/// <summary>
/// Integration tests for JobDispatcherService.
/// Tests job dispatching, recurring job rescheduling, and concurrent execution policies.
/// </summary>
[Collection(nameof(ServicesTestCollection))]
public class JobDispatcherServiceTests(ServicesWebApplicationFactory factory, ITestOutputHelper output) : BackgroundServiceTestBase(factory, output)
{
    [Fact]
    public async Task DispatchDueJobs_ShouldCreateOccurrenceAndPublishToRabbitMQ()
    {
        // Arrange
        await InitializeAsync();

        var uniqueJobName = $"DispatchTestJob_{Guid.CreateVersion7():N}";
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
        await dispatcher.StartAsync(cts.Token);

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

        var uniqueJobName = $"RecurringTestJob_{Guid.CreateVersion7():N}";
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
        await dispatcher.StartAsync(cts.Token);

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

        var uniqueJobName = $"OneTimeJob_{Guid.CreateVersion7():N}";
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
        await dispatcher.StartAsync(cts.Token);

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

        var uniqueJobName = $"InactiveJob_{Guid.CreateVersion7():N}";
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
        await dispatcher.StartAsync(cts.Token);

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

        var uniqueJobName = $"FutureJob_{Guid.CreateVersion7():N}";
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
        await dispatcher.StartAsync(cts.Token);

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
                $"MultiJob{i}_{Guid.CreateVersion7():N}",
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
        await dispatcher.StartAsync(cts.Token);

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

        var uniqueJobName = $"LogTestJob_{Guid.CreateVersion7():N}";
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
        await dispatcher.StartAsync(cts.Token);

        // Wait for occurrence with dispatch log
        var found = await WaitForConditionAsync(
            async () =>
            {
                var dbContext = GetDbContext();
                var occurrenceLogExists = await dbContext.JobOccurrenceLogs
                    .AsNoTracking()
                    .AnyAsync(l => l.Category == "Dispatcher", cts.Token);
                return occurrenceLogExists == true;
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
            .Include(l => l.Logs)
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
        var uniqueJobName = $"VersionTestJob_{Guid.CreateVersion7():N}";
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
        await dispatcher.StartAsync(cts.Token);

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

    [Fact]
    public async Task DispatchDueJobs_ShouldSkipJobWithSkipPolicy_WhenAlreadyRunning()
    {
        // Arrange
        await InitializeAsync();

        var uniqueJobName = $"SkipPolicyJob_{Guid.CreateVersion7():N}";
        var job = await SeedScheduledJobAsync(
            uniqueJobName,
            cronExpression: "0 0 * * * *",
            executeAt: DateTime.UtcNow.AddMinutes(-1)
        );

        // Seed an existing Running occurrence for this job
        await SeedJobOccurrenceAsync(
            jobId: job.Id,
            jobName: job.JobNameInWorker,
            status: JobOccurrenceStatus.Running
        );

        // Mark this job as running in Redis
        var redisScheduler = _serviceProvider.GetRequiredService<IRedisSchedulerService>();
        await redisScheduler.AddToScheduledSetAsync(job.Id, job.ExecuteAt);
        await redisScheduler.CacheJobDetailsAsync(job, TimeSpan.FromHours(1));
        await redisScheduler.MarkJobAsRunningAsync(job.Id, Guid.CreateVersion7());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var dispatcher = CreateJobDispatcherService();
        await dispatcher.StartAsync(cts.Token);

        await Task.Delay(5000, cts.Token);

        await dispatcher.StopAsync(cts.Token);

        // Assert - Should not create a new occurrence because of Skip policy + already running
        var dbContext = GetDbContext();
        var occurrences = await dbContext.JobOccurrences
            .AsNoTracking()
            .Where(o => o.JobId == job.Id && o.Status == JobOccurrenceStatus.Queued)
            .ToListAsync(cts.Token);

        occurrences.Should().BeEmpty("Skip policy should prevent new occurrence when job is already running");
    }

    [Fact]
    public async Task DispatchDueJobs_ShouldCreateOccurrence_WithQueuePolicy()
    {
        // Arrange
        await InitializeAsync();

        var uniqueJobName = $"QueuePolicyJob_{Guid.CreateVersion7():N}";
        var dbContext = GetDbContext();
        var job = new ScheduledJob
        {
            Id = Guid.CreateVersion7(),
            DisplayName = uniqueJobName,
            JobNameInWorker = uniqueJobName,
            JobData = "{}",
            ExecuteAt = DateTime.UtcNow.AddMinutes(-1),
            IsActive = true,
            ConcurrentExecutionPolicy = ConcurrentExecutionPolicy.Queue,
            RoutingPattern = "worker.*",
            CreationDate = DateTime.UtcNow,
            CreatorUserName = "TestUser"
        };

        await dbContext.ScheduledJobs.AddAsync(job);
        await dbContext.SaveChangesAsync();

        var redisScheduler = _serviceProvider.GetRequiredService<IRedisSchedulerService>();
        await redisScheduler.AddToScheduledSetAsync(job.Id, job.ExecuteAt);
        await redisScheduler.CacheJobDetailsAsync(job, TimeSpan.FromHours(1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var dispatcher = CreateJobDispatcherService();
        await dispatcher.StartAsync(cts.Token);

        var found = await WaitForConditionAsync(
            async () =>
            {
                var ctx = GetDbContext();
                return await ctx.JobOccurrences
                    .AsNoTracking()
                    .AnyAsync(o => o.JobId == job.Id, cts.Token);
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await dispatcher.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("Queue policy should create occurrence");

        var dbContextAssert = GetDbContext();
        var createdOccurrence = await dbContextAssert.JobOccurrences
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.JobId == job.Id, cts.Token);

        createdOccurrence.Should().NotBeNull();
        createdOccurrence!.Status.Should().Be(JobOccurrenceStatus.Queued);
    }

    [Fact]
    public async Task DispatchDueJobs_ShouldNotDispatch_WhenDispatcherIsPaused()
    {
        // Arrange
        await InitializeAsync();

        var uniqueJobName = $"PausedDispatcherJob_{Guid.CreateVersion7():N}";
        var job = await SeedScheduledJobAsync(
            uniqueJobName,
            executeAt: DateTime.UtcNow.AddMinutes(-1)
        );

        var redisScheduler = _serviceProvider.GetRequiredService<IRedisSchedulerService>();
        await redisScheduler.AddToScheduledSetAsync(job.Id, job.ExecuteAt);
        await redisScheduler.CacheJobDetailsAsync(job, TimeSpan.FromHours(1));

        var controlService = _serviceProvider.GetRequiredService<IDispatcherControlService>();
        controlService.Stop("Integration test pause", "TestUser");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var dispatcher = CreateJobDispatcherService();
        await dispatcher.StartAsync(cts.Token);

        await Task.Delay(5000, cts.Token);

        await dispatcher.StopAsync(cts.Token);

        // Assert - No occurrence should be created while paused
        var dbContext = GetDbContext();
        var occurrences = await dbContext.JobOccurrences
            .AsNoTracking()
            .Where(o => o.JobId == job.Id)
            .ToListAsync(cts.Token);

        occurrences.Should().BeEmpty("no jobs should be dispatched while dispatcher is paused");

        // Resume for cleanup
        controlService.Resume("TestUser");
    }

    [Fact]
    public async Task DispatchDueJobs_ShouldResumeDispatching_AfterEmergencyStopIsLifted()
    {
        // Arrange
        await InitializeAsync();

        var uniqueJobName = $"ResumeJob_{Guid.CreateVersion7():N}";
        var job = await SeedScheduledJobAsync(
            uniqueJobName,
            executeAt: DateTime.UtcNow.AddMinutes(-1)
        );

        var redisScheduler = _serviceProvider.GetRequiredService<IRedisSchedulerService>();
        await redisScheduler.AddToScheduledSetAsync(job.Id, job.ExecuteAt);
        await redisScheduler.CacheJobDetailsAsync(job, TimeSpan.FromHours(1));

        var controlService = _serviceProvider.GetRequiredService<IDispatcherControlService>();
        controlService.Stop("Temporary pause", "TestUser");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act - Start dispatcher while paused
        var dispatcher = CreateJobDispatcherService();
        await dispatcher.StartAsync(cts.Token);

        // Wait a bit then resume
        await Task.Delay(3000, cts.Token);
        controlService.Resume("TestUser");

        // Wait for job to be dispatched after resume
        var found = await WaitForConditionAsync(
            async () =>
            {
                var ctx = GetDbContext();
                return await ctx.JobOccurrences
                    .AsNoTracking()
                    .AnyAsync(o => o.JobId == job.Id, cts.Token);
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await dispatcher.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("jobs should be dispatched after emergency stop is lifted");
    }

    [Fact]
    public async Task DispatchDueJobs_ShouldCorrectlyReschedule_WithCronExpressionWithSeconds()
    {
        // Arrange
        await InitializeAsync();

        var uniqueJobName = $"CronSecondsJob_{Guid.CreateVersion7():N}";
        var job = await SeedScheduledJobAsync(
            uniqueJobName,
            cronExpression: "*/30 * * * * *", // Every 30 seconds (6-part cron with seconds)
            executeAt: DateTime.UtcNow.AddMinutes(-1)
        );

        var redisScheduler = _serviceProvider.GetRequiredService<IRedisSchedulerService>();
        await redisScheduler.AddToScheduledSetAsync(job.Id, job.ExecuteAt);
        await redisScheduler.CacheJobDetailsAsync(job, TimeSpan.FromHours(1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var dispatcher = CreateJobDispatcherService();
        await dispatcher.StartAsync(cts.Token);

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

        // Assert - Should be rescheduled within 30 seconds from now
        found.Should().BeTrue("cron job with seconds should be rescheduled");

        var nextScheduledTime = await redisScheduler.GetScheduledTimeAsync(job.Id, cts.Token);
        nextScheduledTime.Should().NotBeNull();
        nextScheduledTime.Should().BeAfter(DateTime.UtcNow);
        nextScheduledTime.Should().BeBefore(DateTime.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task DispatchDueJobs_ShouldSetExecutionTimeoutFromJob()
    {
        // Arrange
        await InitializeAsync();

        var dbContext = GetDbContext();
        var uniqueJobName = $"TimeoutTestJob_{Guid.CreateVersion7():N}";
        var job = new ScheduledJob
        {
            Id = Guid.CreateVersion7(),
            DisplayName = uniqueJobName,
            JobNameInWorker = uniqueJobName,
            JobData = "{}",
            ExecuteAt = DateTime.UtcNow.AddMinutes(-1),
            IsActive = true,
            ExecutionTimeoutSeconds = 120,
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

        // Act
        var dispatcher = CreateJobDispatcherService();
        await dispatcher.StartAsync(cts.Token);

        var found = await WaitForConditionAsync(
            async () =>
            {
                var ctx = GetDbContext();
                var occ = await ctx.JobOccurrences
                    .AsNoTracking()
                    .FirstOrDefaultAsync(o => o.JobId == job.Id, cts.Token);
                return occ?.ExecutionTimeoutSeconds == 120;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await dispatcher.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("occurrence should have execution timeout from job definition");

        var dbContextAssert = GetDbContext();
        var occurrence = await dbContextAssert.JobOccurrences
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.JobId == job.Id, cts.Token);

        occurrence.Should().NotBeNull();
        occurrence!.ExecutionTimeoutSeconds.Should().Be(120);
    }

    [Fact]
    public async Task DispatchDueJobs_ShouldPreserveJobData()
    {
        // Arrange
        await InitializeAsync();

        var dbContext = GetDbContext();
        var uniqueJobName = $"JobDataTestJob_{Guid.CreateVersion7():N}";
        var jobData = """{"param1":"value1","param2":42,"nested":{"key":"deep"}}""";
        var job = new ScheduledJob
        {
            Id = Guid.CreateVersion7(),
            DisplayName = uniqueJobName,
            JobNameInWorker = uniqueJobName,
            JobData = jobData,
            ExecuteAt = DateTime.UtcNow.AddMinutes(-1),
            IsActive = true,
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

        // Act
        var dispatcher = CreateJobDispatcherService();
        await dispatcher.StartAsync(cts.Token);

        var found = await WaitForConditionAsync(
            async () =>
            {
                var ctx = GetDbContext();
                return await ctx.JobOccurrences
                    .AsNoTracking()
                    .AnyAsync(o => o.JobId == job.Id, cts.Token);
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await dispatcher.StopAsync(cts.Token);

        // Assert - Verify the job in DB still has its data intact
        found.Should().BeTrue("occurrence should be created");

        var dbContextAssert = GetDbContext();
        var savedJob = await dbContextAssert.ScheduledJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == job.Id, cts.Token);

        savedJob.Should().NotBeNull();

        // Compare as JSON documents since PostgreSQL jsonb normalizes key order
        var expectedJson = JsonDocument.Parse(jobData);
        var actualJson = JsonDocument.Parse(savedJob!.JobData);
        JsonElement.DeepEquals(expectedJson.RootElement, actualJson.RootElement).Should().BeTrue();
    }

    [Fact]
    public async Task DispatchDueJobs_ShouldSetZombieTimeoutOnOccurrence()
    {
        // Arrange
        await InitializeAsync();

        var dbContext = GetDbContext();
        var uniqueJobName = $"ZombieTimeoutJob_{Guid.CreateVersion7():N}";
        var job = new ScheduledJob
        {
            Id = Guid.CreateVersion7(),
            DisplayName = uniqueJobName,
            JobNameInWorker = uniqueJobName,
            JobData = "{}",
            ExecuteAt = DateTime.UtcNow.AddMinutes(-1),
            IsActive = true,
            ZombieTimeoutMinutes = 45,
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

        // Act
        var dispatcher = CreateJobDispatcherService();
        await dispatcher.StartAsync(cts.Token);

        var found = await WaitForConditionAsync(
            async () =>
            {
                var ctx = GetDbContext();
                var occ = await ctx.JobOccurrences
                    .AsNoTracking()
                    .FirstOrDefaultAsync(o => o.JobId == job.Id, cts.Token);
                return occ?.ZombieTimeoutMinutes == 45;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await dispatcher.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("occurrence should inherit zombie timeout from job");

        var dbContextAssert = GetDbContext();
        var occurrence = await dbContextAssert.JobOccurrences
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.JobId == job.Id, cts.Token);

        occurrence.Should().NotBeNull();
        occurrence!.ZombieTimeoutMinutes.Should().Be(45);
    }

    /// <summary>
    /// Builds a dispatcher wired to the fixture's containers.
    ///
    /// Tests await <c>StartAsync</c> rather than queueing it on the thread pool. It returns as soon as the
    /// polling loop yields, so awaiting it does not block - but before that it calls WaitForDatabaseReadyAsync,
    /// which retries every two seconds and only gives up after thirty attempts. Started in the background, that
    /// wait ran inside the window the assertions poll in, so a test measuring "did the dispatcher produce an
    /// occurrence within N seconds" was really racing the dispatcher's own startup, and lost whenever the
    /// database answered slowly under the load of the full suite.
    ///
    /// Awaiting it first means the timeouts below cover dispatching only, which is what these tests are about.
    /// </summary>
    private JobDispatcherService CreateJobDispatcherService() => new(
            _serviceProvider,
            _serviceProvider.GetRequiredService<IRedisSchedulerService>(),
            _serviceProvider.GetRequiredService<IRedisLockService>(),
            _serviceProvider.GetRequiredService<IRedisWorkerService>(),
            _serviceProvider.GetRequiredService<IRedisStatsService>(),
            _serviceProvider.GetRequiredService<IRabbitMQPublisher>(),
            _serviceProvider.GetRequiredService<IAlertNotifier>(),
            Options.Create(new JobDispatcherOptions
            {
                Enabled = true,
                PollingIntervalSeconds = 1,
                BatchSize = 100,
                LockTtlSeconds = 30,
                EnableStartupRecovery = false // Disable for testing
            }),
            _serviceProvider.GetRequiredService<IDispatcherControlService>(),
            _serviceProvider.GetRequiredService<ILoggerFactory>(),
            _serviceProvider.GetRequiredService<BackgroundServiceMetrics>()
        );

    private JobDispatcherService CreateDisabledJobDispatcherService() => new(
            _serviceProvider,
            _serviceProvider.GetRequiredService<IRedisSchedulerService>(),
            _serviceProvider.GetRequiredService<IRedisLockService>(),
            _serviceProvider.GetRequiredService<IRedisWorkerService>(),
            _serviceProvider.GetRequiredService<IRedisStatsService>(),
            _serviceProvider.GetRequiredService<IRabbitMQPublisher>(),
            _serviceProvider.GetRequiredService<IAlertNotifier>(),
            Options.Create(new JobDispatcherOptions
            {
                Enabled = false,
                PollingIntervalSeconds = 1,
                BatchSize = 100,
                LockTtlSeconds = 30,
                EnableStartupRecovery = false
            }),
            _serviceProvider.GetRequiredService<IDispatcherControlService>(),
            _serviceProvider.GetRequiredService<ILoggerFactory>(),
            _serviceProvider.GetRequiredService<BackgroundServiceMetrics>()
        );

    private async Task SeedWorkerInRedisAsync(string workerId, int maxParallelJobs = 5, string jobType = "TestJob", int? consumerMaxParallel = null)
    {
        var workerService = _serviceProvider.GetRequiredService<IRedisWorkerService>();

        var metadata = consumerMaxParallel.HasValue
            ? System.Text.Json.JsonSerializer.Serialize(new
            {
                JobConfigs = new[]
                {
                    new { JobType = jobType, ConsumerId = $"{jobType}-consumer", MaxParallelJobs = consumerMaxParallel.Value, ExecutionTimeoutSeconds = 60 }
                }
            })
            : "{}";

        var registration = new Milvasoft.Milvaion.Sdk.Models.WorkerDiscoveryRequest
        {
            WorkerId = workerId,
            InstanceId = $"{workerId}-test-instance",
            DisplayName = $"Test Worker {workerId}",
            HostName = "test-host",
            IpAddress = "127.0.0.1",
            JobTypes = [jobType],
            RoutingPatterns = new Dictionary<string, string> { [jobType] = $"worker.{workerId}" },
            MaxParallelJobs = maxParallelJobs,
            Version = "1.0.0",
            Metadata = metadata
        };

        await workerService.RegisterWorkerAsync(registration);
    }

    #region Negative / Edge-case Scenarios

    [Fact]
    public async Task DispatchDueJobs_ShouldRemoveFromRedis_WhenJobDeletedFromDatabase()
    {
        // Arrange
        await InitializeAsync();

        var redisScheduler = _serviceProvider.GetRequiredService<IRedisSchedulerService>();
        var orphanJobId = Guid.CreateVersion7();

        // Add job to Redis but NOT to DB (simulates deleted job)
        await redisScheduler.AddToScheduledSetAsync(orphanJobId, DateTime.UtcNow.AddMinutes(-1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var dispatcher = CreateJobDispatcherService();
        await dispatcher.StartAsync(cts.Token);

        // Wait for orphan job to be removed from Redis
        var removed = await WaitForConditionAsync(
            async () =>
            {
                var scheduledTime = await redisScheduler.GetScheduledTimeAsync(orphanJobId, cts.Token);
                return scheduledTime == null;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await dispatcher.StopAsync(cts.Token);

        // Assert
        removed.Should().BeTrue("job not found in DB should be removed from Redis scheduled set");

        var dbContext = GetDbContext();
        var occurrences = await dbContext.JobOccurrences
            .AsNoTracking()
            .Where(o => o.JobId == orphanJobId)
            .ToListAsync(cts.Token);

        occurrences.Should().BeEmpty("no occurrence should be created for deleted job");
    }

    [Fact]
    public async Task DispatchDueJobs_ShouldSkipJob_WhenWorkerAtCapacity()
    {
        // Arrange
        await InitializeAsync();

        // Register a worker with max 1 parallel job
        await SeedWorkerInRedisAsync("cap-worker", maxParallelJobs: 1);

        var workerService = _serviceProvider.GetRequiredService<IRedisWorkerService>();

        // Simulate the worker already running 1 job via heartbeat
        await workerService.UpdateHeartbeatAsync("cap-worker", "cap-worker-test-instance", currentJobs: 1);

        var dbContext = GetDbContext();
        var job = new ScheduledJob
        {
            Id = Guid.CreateVersion7(),
            DisplayName = "CapacityTestJob",
            JobNameInWorker = "TestJob",
            JobData = "{}",
            ExecuteAt = DateTime.UtcNow.AddMinutes(-1),
            IsActive = true,
            WorkerId = "cap-worker",
            RoutingPattern = "worker.cap-worker",
            ConcurrentExecutionPolicy = ConcurrentExecutionPolicy.Queue,
            CreationDate = DateTime.UtcNow,
            CreatorUserName = "TestUser"
        };

        await dbContext.ScheduledJobs.AddAsync(job);
        await dbContext.SaveChangesAsync();

        var redisScheduler = _serviceProvider.GetRequiredService<IRedisSchedulerService>();
        await redisScheduler.AddToScheduledSetAsync(job.Id, job.ExecuteAt);
        await redisScheduler.CacheJobDetailsAsync(job, TimeSpan.FromHours(1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var dispatcher = CreateJobDispatcherService();
        await dispatcher.StartAsync(cts.Token);

        await Task.Delay(5000, cts.Token);

        await dispatcher.StopAsync(cts.Token);

        // Assert - No occurrence should be created since worker is at capacity
        var dbContextAssert = GetDbContext();
        var occurrences = await dbContextAssert.JobOccurrences
            .AsNoTracking()
            .Where(o => o.JobId == job.Id)
            .ToListAsync(cts.Token);

        occurrences.Should().BeEmpty("job should be skipped when worker is at capacity");
    }

    [Fact]
    public async Task DispatchDueJobs_ShouldSkipJob_WhenConsumerAtCapacity()
    {
        // Arrange
        await InitializeAsync();

        // Register a worker with consumer-level capacity of 1 for TestJob
        await SeedWorkerInRedisAsync("cons-worker", maxParallelJobs: 10, jobType: "TestJob", consumerMaxParallel: 1);

        var workerService = _serviceProvider.GetRequiredService<IRedisWorkerService>();

        // Simulate the consumer already running 1 job
        await workerService.IncrementConsumerJobCountAsync("cons-worker", "TestJob");

        var dbContext = GetDbContext();
        var job = new ScheduledJob
        {
            Id = Guid.CreateVersion7(),
            DisplayName = "ConsumerCapacityTestJob",
            JobNameInWorker = "TestJob",
            JobData = "{}",
            ExecuteAt = DateTime.UtcNow.AddMinutes(-1),
            IsActive = true,
            WorkerId = "cons-worker",
            RoutingPattern = "worker.cons-worker",
            ConcurrentExecutionPolicy = ConcurrentExecutionPolicy.Queue,
            CreationDate = DateTime.UtcNow,
            CreatorUserName = "TestUser"
        };

        await dbContext.ScheduledJobs.AddAsync(job);
        await dbContext.SaveChangesAsync();

        var redisScheduler = _serviceProvider.GetRequiredService<IRedisSchedulerService>();
        await redisScheduler.AddToScheduledSetAsync(job.Id, job.ExecuteAt);
        await redisScheduler.CacheJobDetailsAsync(job, TimeSpan.FromHours(1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var dispatcher = CreateJobDispatcherService();
        await dispatcher.StartAsync(cts.Token);

        await Task.Delay(5000, cts.Token);

        await dispatcher.StopAsync(cts.Token);

        // Assert
        var dbContextAssert = GetDbContext();
        var occurrences = await dbContextAssert.JobOccurrences
            .AsNoTracking()
            .Where(o => o.JobId == job.Id)
            .ToListAsync(cts.Token);

        occurrences.Should().BeEmpty("job should be skipped when consumer is at capacity");
    }

    [Fact]
    public async Task DispatchDueJobs_ShouldSkipJob_WhenAssignedToNonExistentWorker()
    {
        // Arrange
        await InitializeAsync();

        var dbContext = GetDbContext();
        var job = new ScheduledJob
        {
            Id = Guid.CreateVersion7(),
            DisplayName = "GhostWorkerJob",
            JobNameInWorker = "TestJob",
            JobData = "{}",
            ExecuteAt = DateTime.UtcNow.AddMinutes(-1),
            IsActive = true,
            WorkerId = "non-existent-worker",
            RoutingPattern = "worker.non-existent-worker",
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

        // Act
        var dispatcher = CreateJobDispatcherService();
        await dispatcher.StartAsync(cts.Token);

        await Task.Delay(5000, cts.Token);

        await dispatcher.StopAsync(cts.Token);

        // Assert
        var dbContextAssert = GetDbContext();
        var occurrences = await dbContextAssert.JobOccurrences
            .AsNoTracking()
            .Where(o => o.JobId == job.Id)
            .ToListAsync(cts.Token);

        occurrences.Should().BeEmpty("job should be skipped when assigned to non-existent worker");
    }

    [Fact]
    public async Task DispatchDueJobs_ShouldRemoveFromRedis_WhenCronExpressionIsInvalid()
    {
        // Arrange
        await InitializeAsync();

        var dbContext = GetDbContext();
        var job = new ScheduledJob
        {
            Id = Guid.CreateVersion7(),
            DisplayName = "InvalidCronJob",
            JobNameInWorker = "TestJob",
            JobData = "{}",
            ExecuteAt = DateTime.UtcNow.AddMinutes(-1),
            IsActive = true,
            CronExpression = "INVALID CRON",
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

        // Act
        var dispatcher = CreateJobDispatcherService();
        await dispatcher.StartAsync(cts.Token);

        // Wait for job to be removed from Redis (invalid cron should cause removal)
        var removed = await WaitForConditionAsync(
            async () =>
            {
                var scheduledTime = await redisScheduler.GetScheduledTimeAsync(job.Id, cts.Token);
                return scheduledTime == null;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await dispatcher.StopAsync(cts.Token);

        // Assert
        removed.Should().BeTrue("job with invalid cron expression should be removed from Redis");
    }

    [Fact]
    public async Task DispatchDueJobs_ShouldNotDispatch_ExternalJobs()
    {
        // Arrange
        await InitializeAsync();

        var dbContext = GetDbContext();
        var job = new ScheduledJob
        {
            Id = Guid.CreateVersion7(),
            DisplayName = "External Quartz Job",
            JobNameInWorker = "QuartzJob",
            JobData = "{}",
            ExecuteAt = DateTime.UtcNow.AddMinutes(-1),
            IsActive = true,
            IsExternal = true,
            ExternalJobId = "DEFAULT.QuartzJob",
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

        // Act
        var dispatcher = CreateJobDispatcherService();
        await dispatcher.StartAsync(cts.Token);

        await Task.Delay(5000, cts.Token);

        await dispatcher.StopAsync(cts.Token);

        // Assert
        var dbContextAssert = GetDbContext();
        var occurrences = await dbContextAssert.JobOccurrences
            .AsNoTracking()
            .Where(o => o.JobId == job.Id)
            .ToListAsync(cts.Token);

        occurrences.Should().BeEmpty("external jobs should never be dispatched by JobDispatcher");
    }

    [Fact]
    public async Task DispatchDueJobs_ShouldCleanUpStaleRedisEntries_WhenJobsNotInDatabase()
    {
        // Arrange
        await InitializeAsync();

        var redisScheduler = _serviceProvider.GetRequiredService<IRedisSchedulerService>();

        // Create multiple orphan entries in Redis (not in DB)
        var staleIds = new List<Guid>();
        for (int i = 0; i < 3; i++)
        {
            var staleId = Guid.CreateVersion7();
            staleIds.Add(staleId);
            await redisScheduler.AddToScheduledSetAsync(staleId, DateTime.UtcNow.AddMinutes(-1));
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var dispatcher = CreateJobDispatcherService();
        await dispatcher.StartAsync(cts.Token);

        // Wait for all stale entries to be cleaned up
        var allCleaned = await WaitForConditionAsync(
            async () =>
            {
                foreach (var id in staleIds)
                {
                    var time = await redisScheduler.GetScheduledTimeAsync(id, cts.Token);

                    if (time != null)
                        return false;
                }

                return true;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await dispatcher.StopAsync(cts.Token);

        // Assert
        allCleaned.Should().BeTrue("all stale Redis entries should be cleaned up");
    }

    [Fact]
    public async Task StartAsync_ShouldNotStart_WhenDisabledInOptions()
    {
        // Arrange
        await InitializeAsync();

        var uniqueJobName = $"DisabledDispatcherJob_{Guid.CreateVersion7():N}";
        var job = await SeedScheduledJobAsync(uniqueJobName, executeAt: DateTime.UtcNow.AddMinutes(-1));

        // Intentionally do NOT add job to Redis scheduled set.
        // This test verifies the disabled dispatcher never starts its polling loop,
        // so it should never reach Redis at all. Adding to Redis would risk
        // cross-test interference from other dispatchers still running.

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act - Create with Enabled = false
        var dispatcher = CreateDisabledJobDispatcherService();
        await dispatcher.StartAsync(cts.Token);

        // Wait some time - dispatcher should not process
        await Task.Delay(3000, cts.Token);

        // Assert - No occurrences should be created
        var dbContext = GetDbContext();
        var occurrences = await dbContext.JobOccurrences
            .AsNoTracking()
            .Where(o => o.JobId == job.Id)
            .ToListAsync(cts.Token);

        occurrences.Should().BeEmpty("disabled dispatcher should not process any jobs");
    }

    #endregion
}
