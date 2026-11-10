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
using Milvaion.Infrastructure.Persistence;
using Milvaion.Infrastructure.Services;
using Milvaion.Infrastructure.Services.Redis;
using Milvaion.Infrastructure.Telemetry;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.BackgroundServices;

/// <summary>
/// Integration tests for claim, retry and concurrency guarantees between scheduler components.
/// </summary>
[Collection(nameof(ServicesTestCollection))]
public class DispatchReliabilityTests(ServicesWebApplicationFactory factory, ITestOutputHelper output) : BackgroundServiceTestBase(factory, output)
{
    private sealed class FakePublisher(bool publishResult) : IRabbitMQPublisher
    {
        public System.Collections.Concurrent.ConcurrentBag<(Guid JobId, Guid OccurrenceId)> Published { get; } = [];

        public Task<bool> PublishJobAsync(ScheduledJob job, Guid occurrenceId, CancellationToken cancellationToken = default)
        {
            Published.Add((job.Id, occurrenceId));

            return Task.FromResult(publishResult);
        }

        public Task<int> PublishBatchAsync(Dictionary<ScheduledJob, Guid> jobsWithCorrelation, CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<uint> GetQueueMessageCountAsync(string routingPattern, CancellationToken cancellationToken = default) => Task.FromResult(0u);
    }

    private JobDispatcherService CreateDispatcher(IRabbitMQPublisher publisher = null) => new(
        _serviceProvider,
        _serviceProvider.GetRequiredService<IRedisSchedulerService>(),
        _serviceProvider.GetRequiredService<IRedisLockService>(),
        _serviceProvider.GetRequiredService<IRedisWorkerService>(),
        _serviceProvider.GetRequiredService<IRedisStatsService>(),
        publisher ?? _serviceProvider.GetRequiredService<IRabbitMQPublisher>(),
        _serviceProvider.GetRequiredService<IAlertNotifier>(),
        Options.Create(new JobDispatcherOptions
        {
            Enabled = true,
            PollingIntervalSeconds = 1,
            BatchSize = 100,
            LockTtlSeconds = 30,
            EnableStartupRecovery = false
        }),
        _serviceProvider.GetRequiredService<IDispatcherControlService>(),
        _serviceProvider.GetRequiredService<ILoggerFactory>(),
        _serviceProvider.GetRequiredService<BackgroundServiceMetrics>());

    [Fact]
    public async Task TwoDispatchers_ShouldCreateOneOccurrence_ForOneDueSlot()
    {
        // Arrange
        await InitializeAsync();

        // Fires once a year, so a second occurrence can only come from a double dispatch.
        var job = await SeedScheduledJobAsync($"RaceJob_{Guid.CreateVersion7():N}", cronExpression: "0 0 0 1 1 *", executeAt: DateTime.UtcNow.AddMinutes(-1));

        var scheduler = _serviceProvider.GetRequiredService<IRedisSchedulerService>();

        await scheduler.AddToScheduledSetAsync(job.Id, job.ExecuteAt);
        await scheduler.CacheJobDetailsAsync(job, TimeSpan.FromHours(1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var first = CreateDispatcher();
        var second = CreateDispatcher();

        // Act
        await Task.WhenAll(first.StartAsync(cts.Token), second.StartAsync(cts.Token));

        await WaitForConditionAsync(async () => await GetDbContext().JobOccurrences.AsNoTracking().AnyAsync(o => o.JobId == job.Id, cts.Token),
                                    TimeSpan.FromSeconds(15),
                                    TimeSpan.FromMilliseconds(200),
                                    cts.Token);

        // Further polls of both instances must not add anything.
        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);

        await Task.WhenAll(first.StopAsync(cts.Token), second.StopAsync(cts.Token));

        // Assert
        var count = await GetDbContext().JobOccurrences.AsNoTracking().CountAsync(o => o.JobId == job.Id, cts.Token);

        count.Should().Be(1);
    }

    [Fact]
    public async Task PublishFailure_ShouldStayQueuedForRetry_WithoutCreatingMoreOccurrences()
    {
        // Arrange
        await InitializeAsync();

        var job = await SeedScheduledJobAsync($"UnroutableJob_{Guid.CreateVersion7():N}", cronExpression: "0 0 0 1 1 *", executeAt: DateTime.UtcNow.AddMinutes(-1));

        var failingPublisher = new FakePublisher(publishResult: false);

        var scheduler = _serviceProvider.GetRequiredService<IRedisSchedulerService>();

        await scheduler.AddToScheduledSetAsync(job.Id, job.ExecuteAt);
        await scheduler.CacheJobDetailsAsync(job, TimeSpan.FromHours(1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var dispatcher = CreateDispatcher(failingPublisher);

        // Act
        await dispatcher.StartAsync(cts.Token);

        await WaitForConditionAsync(async () => await GetDbContext().JobOccurrences.AsNoTracking().AnyAsync(o => o.JobId == job.Id && o.NextDispatchRetryAt != null, cts.Token),
                                    TimeSpan.FromSeconds(15),
                                    TimeSpan.FromMilliseconds(200),
                                    cts.Token);

        await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);

        await dispatcher.StopAsync(cts.Token);

        // Assert
        var occurrences = await GetDbContext().JobOccurrences.AsNoTracking().Where(o => o.JobId == job.Id).ToListAsync(cts.Token);

        occurrences.Should().ContainSingle("the schedule moves on, and the retry re-sends the same occurrence");
        occurrences[0].Status.Should().Be(JobOccurrenceStatus.Queued);
        occurrences[0].DispatchRetryCount.Should().Be(1);
        occurrences[0].NextDispatchRetryAt.Should().NotBeNull();

        var score = await scheduler.GetScheduledTimeAsync(job.Id, cts.Token);

        score.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task DueRetry_ShouldRepublishAndClearRetryState_EvenWithoutDueJobs()
    {
        // Arrange
        await InitializeAsync();

        var job = await SeedScheduledJobAsync($"RetryJob_{Guid.CreateVersion7():N}");
        var occurrence = await SeedJobOccurrenceAsync(job.Id, status: JobOccurrenceStatus.Queued);

        var dbContext = GetDbContext();

        await dbContext.JobOccurrences.Where(o => o.Id == occurrence.Id)
                       .ExecuteUpdateAsync(s => s.SetProperty(o => o.NextDispatchRetryAt, DateTime.UtcNow.AddSeconds(-1))
                                                 .SetProperty(o => o.DispatchRetryCount, 1)
                                                 .SetProperty(o => o.Exception, "Failed to publish to RabbitMQ"));

        var publisher = new FakePublisher(publishResult: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var dispatcher = CreateDispatcher(publisher);

        // Act
        await dispatcher.StartAsync(cts.Token);

        var cleared = await WaitForConditionAsync(async () => (await GetDbContext().JobOccurrences.AsNoTracking().FirstAsync(o => o.Id == occurrence.Id, cts.Token)).NextDispatchRetryAt == null,
                                                  TimeSpan.FromSeconds(15),
                                                  TimeSpan.FromMilliseconds(200),
                                                  cts.Token);

        await dispatcher.StopAsync(cts.Token);

        // Assert
        cleared.Should().BeTrue();

        var stored = await GetOccurrenceAsync(occurrence.Id);

        stored.Status.Should().Be(JobOccurrenceStatus.Queued);
        stored.Exception.Should().BeNull();

        var storedJob = await GetDbContext().ScheduledJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id, cts.Token);

        storedJob.CompletedAt.Should().NotBeNull("a one-time job that was finally published must not be scheduled again after a restart");
    }

    [Fact]
    public async Task BulkUpdateGuarded_ShouldNotOverwriteRow_WhenGuardRejectsLockedState()
    {
        // Arrange
        await InitializeAsync();

        var job = await SeedScheduledJobAsync($"GuardJob_{Guid.CreateVersion7():N}");
        var completed = await SeedJobOccurrenceAsync(job.Id, status: JobOccurrenceStatus.Completed);
        var running = await SeedJobOccurrenceAsync(job.Id, status: JobOccurrenceStatus.Queued);

        var staleUpdates = new List<JobOccurrence>
        {
            new() { Id = completed.Id, JobId = job.Id, CorrelationId = completed.CorrelationId, Status = JobOccurrenceStatus.Running },
            new() { Id = running.Id, JobId = job.Id, CorrelationId = running.CorrelationId, Status = JobOccurrenceStatus.Running }
        };

        // Act
        var rejected = await GetDbContext().BulkUpdateGuardedAsync(staleUpdates,
                                                                   (_, row) => row.Status != JobOccurrenceStatus.Completed,
                                                                   bc => bc.PropertiesToIncludeOnUpdate = [nameof(JobOccurrence.Status)],
                                                                   CancellationToken.None);

        // Assert
        rejected.Should().ContainSingle().Which.Id.Should().Be(completed.Id);

        (await GetOccurrenceAsync(completed.Id)).Status.Should().Be(JobOccurrenceStatus.Completed);
        (await GetOccurrenceAsync(running.Id)).Status.Should().Be(JobOccurrenceStatus.Running);
    }

    [Fact]
    public async Task EmergencyStop_ShouldReachOtherInstances_AndClearOnResume()
    {
        // Arrange
        await InitializeAsync();

        DispatcherControlService CreateControl() => new(_serviceProvider.GetRequiredService<ILoggerFactory>(),
                                                        Options.Create(new JobDispatcherOptions { Enabled = true }),
                                                        _serviceProvider.GetRequiredService<RedisConnectionService>(),
                                                        _serviceProvider.GetRequiredService<IOptions<RedisOptions>>());

        var instanceA = CreateControl();
        var instanceB = CreateControl();

        // Act & Assert
        instanceA.Stop("maintenance", "tester");

        try
        {
            await instanceB.RefreshAsync();

            instanceB.IsEnabled.Should().BeFalse();
            instanceB.GetStopReason().Should().Contain("maintenance");
        }
        finally
        {
            instanceA.Resume("tester");
        }

        await instanceB.RefreshAsync();

        instanceB.IsEnabled.Should().BeTrue();
    }
}
