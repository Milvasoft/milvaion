using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Interfaces.RabbitMQ;
using Milvaion.Application.Interfaces.Redis;
using Milvaion.Application.Utils.Models.Options;
using Milvaion.Domain;
using Milvaion.Infrastructure.BackgroundServices;
using Milvaion.Infrastructure.Telemetry;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Utils;
using RabbitMQ.Client;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.BackgroundServices;

/// <summary>
/// Dispatcher behaviour for exclusion calendars, the missed-run policy and job priority.
/// </summary>
[Collection(nameof(ServicesTestCollection))]
public class SchedulingDispatchTests(ServicesWebApplicationFactory factory, ITestOutputHelper output) : BackgroundServiceTestBase(factory, output)
{
    private const string _everyMinute = "0 * * * * *";

    private JobDispatcherService CreateDispatcher() => new(
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
                EnableStartupRecovery = false
            }),
            _serviceProvider.GetRequiredService<IDispatcherControlService>(),
            _serviceProvider.GetRequiredService<ILoggerFactory>(),
            _serviceProvider.GetRequiredService<BackgroundServiceMetrics>());

    private async Task<ScheduledJob> SeedAsync(DateTime executeAt,
                                               string cron = _everyMinute,
                                               MissedRunPolicy policy = MissedRunPolicy.RunOnce,
                                               int priority = 0,
                                               List<Guid> calendarIds = null,
                                               string routingPattern = "worker.*")
    {
        var job = new ScheduledJob
        {
            Id = Guid.CreateVersion7(),
            DisplayName = $"Scheduling {Guid.NewGuid():N}",
            JobNameInWorker = "TestJob",
            JobData = "{}",
            ExecuteAt = executeAt,
            CronExpression = cron,
            IsActive = true,
            RoutingPattern = routingPattern,
            ConcurrentExecutionPolicy = ConcurrentExecutionPolicy.Queue,
            MissedRunPolicy = policy,
            Priority = priority,
            ExclusionCalendarIds = calendarIds ?? [],
            CreationDate = DateTime.UtcNow,
            CreatorUserName = "TestUser"
        };

        var dbContext = GetDbContext();
        await dbContext.ScheduledJobs.AddAsync(job);
        await dbContext.SaveChangesAsync();

        var redis = _serviceProvider.GetRequiredService<IRedisSchedulerService>();
        await redis.AddToScheduledSetAsync(job.Id, job.ExecuteAt);
        await redis.CacheJobDetailsAsync(job, TimeSpan.FromHours(1));

        return job;
    }

    private async Task<List<JobOccurrence>> WaitForOccurrencesAsync(Guid jobId, int atLeast = 1, int timeoutSeconds = 15)
    {
        List<JobOccurrence> occurrences = [];

        await WaitForConditionAsync(async () =>
        {
            occurrences = await GetDbContext().JobOccurrences.AsNoTracking().Where(o => o.JobId == jobId).ToListAsync();
            return occurrences.Count >= atLeast;
        }, TimeSpan.FromSeconds(timeoutSeconds), TimeSpan.FromMilliseconds(300));

        return occurrences;
    }

    private static DateTime MinuteFloor(DateTime utc) => new(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Dispatch_ShouldRecordSkippedOccurrence_WhenRunFallsOnExcludedDay()
    {
        await InitializeAsync();

        var due = MinuteFloor(DateTime.UtcNow);
        var calendar = new ExclusionCalendar { Id = Guid.CreateVersion7(), Name = $"Today {Guid.NewGuid():N}", Dates = [DateOnly.FromDateTime(due)] };

        var dbContext = GetDbContext();
        await dbContext.ExclusionCalendars.AddAsync(calendar);
        await dbContext.SaveChangesAsync();

        var job = await SeedAsync(due, calendarIds: [calendar.Id]);

        var dispatcher = CreateDispatcher();
        await dispatcher.StartAsync(CancellationToken.None);

        var occurrences = await WaitForOccurrencesAsync(job.Id);

        // The dispatcher persists the occurrence first and its logs at the end of the same cycle. Stopping it as soon
        // as the occurrence shows up can cancel that cycle before the logs are written, so wait for them first.
        List<JobOccurrenceLog> logs = [];
        var skippedOccurrenceId = occurrences[0].Id;

        await WaitForConditionAsync(async () =>
        {
            logs = await GetDbContext().JobOccurrenceLogs.AsNoTracking().Where(l => l.OccurrenceId == skippedOccurrenceId).ToListAsync();
            return logs.Count > 0;
        }, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200));

        await dispatcher.StopAsync(CancellationToken.None);

        occurrences.Should().ContainSingle();
        occurrences[0].Status.Should().Be(JobOccurrenceStatus.Skipped);
        occurrences[0].Result.Should().Contain(calendar.Name);

        logs.Should().Contain(l => l.Category == "Scheduler" && l.Message.Contains("excluded by calendar"));

        // The schedule jumped past the excluded day.
        var next = await _serviceProvider.GetRequiredService<IRedisSchedulerService>().GetScheduledTimeAsync(job.Id);
        next.Should().NotBeNull();
        DateOnly.FromDateTime(next.Value).Should().BeAfter(DateOnly.FromDateTime(due));
    }

    [Fact]
    public async Task Dispatch_ShouldSkipMissedRuns_WithSkipPolicy()
    {
        await InitializeAsync();

        var now = DateTime.UtcNow;
        await SimulateDispatcherDownSinceAsync(now.AddMinutes(-10));

        var job = await SeedAsync(MinuteFloor(now.AddMinutes(-5)), policy: MissedRunPolicy.Skip);

        var dispatcher = CreateDispatcher();
        await dispatcher.StartAsync(CancellationToken.None);

        var occurrences = await WaitForOccurrencesAsync(job.Id);

        await Task.Delay(TimeSpan.FromSeconds(3)); // give it a few more polls to prove nothing else runs

        await dispatcher.StopAsync(CancellationToken.None);

        occurrences = await GetDbContext().JobOccurrences.AsNoTracking().Where(o => o.JobId == job.Id).ToListAsync();

        // Exactly one skipped record for the missed runs and no catch-up. When the extra wait crosses a minute
        // boundary the next regular run starts as well; it is created after that boundary and is not a catch-up.
        var skipped = occurrences.Where(o => o.Status == JobOccurrenceStatus.Skipped).ToList();
        skipped.Should().ContainSingle();
        skipped[0].Result.Should().Contain("missed while the scheduler was down");
        occurrences.Where(o => o.Status != JobOccurrenceStatus.Skipped)
                   .Should().NotContain(o => o.CreatedAt < MinuteFloor(now).AddMinutes(1), "Skip runs nothing for the missed runs");

        var next = await _serviceProvider.GetRequiredService<IRedisSchedulerService>().GetScheduledTimeAsync(job.Id);
        next.Should().BeAfter(now);
    }

    [Fact]
    public async Task Dispatch_ShouldRunOneCatchUp_WithRunOncePolicy()
    {
        await InitializeAsync();

        var now = DateTime.UtcNow;
        await SimulateDispatcherDownSinceAsync(now.AddMinutes(-10));

        var job = await SeedAsync(MinuteFloor(now.AddMinutes(-5)), policy: MissedRunPolicy.RunOnce);

        var dispatcher = CreateDispatcher();
        await dispatcher.StartAsync(CancellationToken.None);

        await WaitForOccurrencesAsync(job.Id);

        await Task.Delay(TimeSpan.FromSeconds(3));

        await dispatcher.StopAsync(CancellationToken.None);

        var occurrences = await GetDbContext().JobOccurrences.AsNoTracking().Where(o => o.JobId == job.Id && o.Status != JobOccurrenceStatus.Skipped).ToListAsync();

        // One catch-up; the next regular run is in the future (unless the minute rolled over during the test).
        occurrences.Should().HaveCountGreaterThanOrEqualTo(1);

        var firstId = occurrences.OrderBy(o => o.CreatedAt).First().Id;

        var catchUpLogs = await GetDbContext().JobOccurrenceLogs.AsNoTracking()
                                              .Where(l => l.OccurrenceId == firstId && l.Category == "Scheduler")
                                              .ToListAsync();

        catchUpLogs.Should().ContainSingle(l => l.Message.StartsWith("Catch-up run"));

        // Count the catch-ups by their log, not by a time window: when the test crosses a minute boundary the next
        // regular run is created a few seconds after "now" and a window would count it as a second catch-up.
        var occurrenceIds = occurrences.Select(o => o.Id).ToList();
        var catchUpRuns = await GetDbContext().JobOccurrenceLogs.AsNoTracking()
                                              .Where(l => occurrenceIds.Contains(l.OccurrenceId) && l.Category == "Scheduler" && l.Message.StartsWith("Catch-up run"))
                                              .Select(l => l.OccurrenceId)
                                              .Distinct()
                                              .CountAsync();

        catchUpRuns.Should().Be(1, "RunOnce runs a single catch-up for all the missed runs");
    }

    [Fact]
    public async Task Dispatch_ShouldRunLateJobNormally_WhenThereWasNoOutage()
    {
        await InitializeAsync();

        // No outage recorded: a late run is not a missed one, so even the Skip policy runs it.
        var job = await SeedAsync(MinuteFloor(DateTime.UtcNow.AddMinutes(-5)), policy: MissedRunPolicy.Skip);

        var dispatcher = CreateDispatcher();
        await dispatcher.StartAsync(CancellationToken.None);

        var occurrences = await WaitForOccurrencesAsync(job.Id);

        await dispatcher.StopAsync(CancellationToken.None);

        occurrences.Should().Contain(o => o.Status != JobOccurrenceStatus.Skipped);
    }

    [Fact]
    public async Task Dispatch_ShouldSetAmqpPriority_AndSendHigherPriorityFirst()
    {
        await InitializeAsync();

        var routingKey = $"prio.{Guid.NewGuid():N}";
        var queueName = $"test-priority-{Guid.NewGuid():N}";

        var connectionFactory = new ConnectionFactory
        {
            HostName = _factory.GetRabbitMqHost(),
            Port = _factory.GetRabbitMqPort(),
            UserName = "guest",
            Password = "guest"
        };

        await using var connection = await connectionFactory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        await channel.ExchangeDeclareAsync(WorkerConstant.ExchangeName, ExchangeType.Topic, durable: true);
        await channel.QueueDeclareAsync(queueName, durable: false, exclusive: false, autoDelete: true);
        await channel.QueueBindAsync(queueName, WorkerConstant.ExchangeName, routingKey);

        // The high priority job is seeded first: the host's own dispatcher also polls and may pick a job up between
        // the two seeds. Seeded this way the high one still goes out first, in one cycle or in two.
        var due = DateTime.UtcNow.AddSeconds(-5);
        var high = await SeedAsync(due.AddSeconds(1), cron: null, priority: 8, routingPattern: routingKey);
        var low = await SeedAsync(due, cron: null, priority: 1, routingPattern: routingKey);

        var dispatcher = CreateDispatcher();
        await dispatcher.StartAsync(CancellationToken.None);

        var received = new List<(string MessageId, byte Priority)>();

        await WaitForConditionAsync(async () =>
        {
            var message = await channel.BasicGetAsync(queueName, autoAck: true);

            if (message is not null)
                received.Add((message.BasicProperties.MessageId, message.BasicProperties.Priority));

            return received.Count >= 2;
        }, TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(200));

        await dispatcher.StopAsync(CancellationToken.None);

        received.Should().HaveCount(2);
        received[0].Should().Be((high.Id.ToString(), (byte)8));
        received[1].Should().Be((low.Id.ToString(), (byte)1));
    }
}
