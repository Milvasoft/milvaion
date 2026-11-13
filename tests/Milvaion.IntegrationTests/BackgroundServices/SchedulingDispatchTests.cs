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

        await dispatcher.StopAsync(CancellationToken.None);

        occurrences.Should().ContainSingle();
        occurrences[0].Status.Should().Be(JobOccurrenceStatus.Skipped);
        occurrences[0].Result.Should().Contain(calendar.Name);

        var logs = await GetDbContext().JobOccurrenceLogs.AsNoTracking().Where(l => l.OccurrenceId == occurrences[0].Id).ToListAsync();
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

        occurrences.Should().ContainSingle();
        occurrences[0].Status.Should().Be(JobOccurrenceStatus.Skipped);
        occurrences[0].Result.Should().Contain("missed while the scheduler was down");

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
        occurrences.Count(o => o.CreatedAt < now.AddSeconds(50)).Should().Be(1, "RunOnce runs a single catch-up for all the missed runs");
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

        var due = DateTime.UtcNow.AddSeconds(-5);
        var low = await SeedAsync(due, cron: null, priority: 1, routingPattern: routingKey);
        var high = await SeedAsync(due.AddSeconds(1), cron: null, priority: 8, routingPattern: routingKey);

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
