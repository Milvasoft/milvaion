using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Utils;
using RabbitMQ.Client;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.BackgroundServices;

/// <summary>
/// Base class for background service integration tests.
/// Provides utilities for starting/stopping hosted services and seeding test data.
/// </summary>
public abstract class BackgroundServiceTestBase(CustomWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    /// <inheritdoc/>
    /// <remarks>
    /// Also forgets the dispatcher heartbeat and the last recorded outage. Redis is shared by every test in the
    /// collection, so without this the gap between one test's dispatcher and the next would be recorded as an
    /// outage, and a recurring job seeded in the past would be treated as missed and skipped.
    /// </remarks>
    public override async Task InitializeAsync(Action<IServiceCollection> configureServices = null, Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> configureApp = null)
    {
        await base.InitializeAsync(configureServices, configureApp);

        await ResetDispatcherHeartbeatAsync();
    }

    /// <summary>
    /// Deletes the dispatcher heartbeat and outage keys.
    /// </summary>
    protected async Task ResetDispatcherHeartbeatAsync()
    {
        var (lastPollKey, outageKey) = DispatcherHeartbeatKeys();
        var db = _serviceProvider.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>().GetDatabase();

        await db.KeyDeleteAsync([lastPollKey, outageKey]);
    }

    /// <summary>
    /// Makes the next dispatcher poll see a gap since <paramref name="lastPollUtc"/>, i.e. an outage that started then.
    /// </summary>
    protected async Task SimulateDispatcherDownSinceAsync(DateTime lastPollUtc)
    {
        var (lastPollKey, outageKey) = DispatcherHeartbeatKeys();
        var db = _serviceProvider.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>().GetDatabase();

        await db.KeyDeleteAsync(outageKey);
        await db.StringSetAsync(lastPollKey, new DateTimeOffset(lastPollUtc).ToUnixTimeMilliseconds());
    }

    private (StackExchange.Redis.RedisKey LastPoll, StackExchange.Redis.RedisKey Outage) DispatcherHeartbeatKeys()
    {
        var prefix = _serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Milvaion.Application.Utils.Models.Options.RedisOptions>>().Value.KeyPrefix;

        return ($"{prefix}dispatcher:last_poll", $"{prefix}dispatcher:last_outage");
    }

    /// <summary>
    /// Starts a specific hosted service manually for testing.
    /// </summary>
    protected async Task<TService> StartBackgroundServiceAsync<TService>(CancellationToken cancellationToken = default) where TService : class, IHostedService
    {
        var service = _serviceProvider.GetRequiredService<TService>();
        await service.StartAsync(cancellationToken);
        return service;
    }

    /// <summary>
    /// Stops a hosted service after testing.
    /// </summary>
    protected static async Task StopBackgroundServiceAsync<TService>(TService service, CancellationToken cancellationToken = default) where TService : class, IHostedService
    {
        if (service != null)
            await service.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Purges all RabbitMQ queues to ensure clean test state.
    /// </summary>
    protected async Task PurgeAllQueuesAsync()
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

            var queuesToPurge = new[]
            {
                WorkerConstant.Queues.WorkerLogs,
                WorkerConstant.Queues.StatusUpdates,
                WorkerConstant.Queues.FailedOccurrences,
                WorkerConstant.Queues.WorkerRegistration,
                WorkerConstant.Queues.WorkerHeartbeat,
                WorkerConstant.Queues.ExternalJobRegistration,
                WorkerConstant.Queues.ExternalJobOccurrence
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

    /// <summary>
    /// Seeds a scheduled job for testing.
    /// </summary>
    protected async Task<ScheduledJob> SeedScheduledJobAsync(
        string jobName = "TestJob",
        string workerId = null,
        string cronExpression = null,
        DateTime? executeAt = null,
        bool isActive = true,
        int? timeoutMinutes = null,
        CancellationToken cancellationToken = default)
    {
        var dbContext = GetDbContext();

        var job = new ScheduledJob
        {
            Id = Guid.CreateVersion7(),
            DisplayName = $"Test {jobName}",
            Description = $"Test job for {jobName}",
            JobNameInWorker = jobName,
            JobData = "{}",
            ExecuteAt = executeAt ?? DateTime.UtcNow.AddMinutes(-5), // Due in past for immediate dispatch
            CronExpression = cronExpression,
            IsActive = isActive,
            WorkerId = workerId,
            RoutingPattern = workerId != null ? $"worker.{workerId}" : "worker.*",
            ZombieTimeoutMinutes = timeoutMinutes,
            ConcurrentExecutionPolicy = ConcurrentExecutionPolicy.Skip,
            CreationDate = DateTime.UtcNow,
            CreatorUserName = "TestUser"
        };

        await dbContext.ScheduledJobs.AddAsync(job, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        return job;
    }

    /// <summary>
    /// Seeds a job occurrence for testing.
    /// </summary>
    protected async Task<JobOccurrence> SeedJobOccurrenceAsync(
        Guid jobId,
        string jobName = "TestJob",
        JobOccurrenceStatus status = JobOccurrenceStatus.Queued,
        DateTime? createdAt = null,
        string workerId = null,
        int? timeoutMinutes = null,
        CancellationToken cancellationToken = default)
    {
        var dbContext = GetDbContext();

        var correlationId = Guid.CreateVersion7();
        var occurrence = new JobOccurrence
        {
            Id = correlationId,
            CorrelationId = correlationId,
            JobId = jobId,
            JobName = jobName,
            JobVersion = 1,
            Status = status,
            WorkerId = workerId,
            ZombieTimeoutMinutes = timeoutMinutes,
            LastHeartbeat = createdAt ?? DateTime.UtcNow,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            Logs =
            [
                new JobOccurrenceLog
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Information",
                    Message = "Test occurrence created",
                    Category = "Test"
                }
            ]
        };

        await dbContext.JobOccurrences.AddAsync(occurrence, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        return occurrence;
    }

    /// <summary>
    /// Seeds multiple job occurrences in different states for testing.
    /// </summary>
    protected async Task<List<JobOccurrence>> SeedMultipleOccurrencesAsync(
        Guid jobId,
        int queuedCount = 0,
        int runningCount = 0,
        int completedCount = 0,
        int failedCount = 0,
        DateTime? baseCreatedAt = null,
        CancellationToken cancellationToken = default)
    {
        var occurrences = new List<JobOccurrence>();
        var baseTime = baseCreatedAt ?? DateTime.UtcNow;

        for (int i = 0; i < queuedCount; i++)
        {
            occurrences.Add(await SeedJobOccurrenceAsync(
                jobId,
                status: JobOccurrenceStatus.Queued,
                createdAt: baseTime.AddMinutes(-i),
                cancellationToken: cancellationToken));
        }

        for (int i = 0; i < runningCount; i++)
        {
            occurrences.Add(await SeedJobOccurrenceAsync(
                jobId,
                status: JobOccurrenceStatus.Running,
                createdAt: baseTime.AddMinutes(-i),
                cancellationToken: cancellationToken));
        }

        for (int i = 0; i < completedCount; i++)
        {
            occurrences.Add(await SeedJobOccurrenceAsync(
                jobId,
                status: JobOccurrenceStatus.Completed,
                createdAt: baseTime.AddMinutes(-i),
                cancellationToken: cancellationToken));
        }

        for (int i = 0; i < failedCount; i++)
        {
            occurrences.Add(await SeedJobOccurrenceAsync(
                jobId,
                status: JobOccurrenceStatus.Failed,
                createdAt: baseTime.AddMinutes(-i),
                cancellationToken: cancellationToken));
        }

        return occurrences;
    }

    /// <summary>
    /// Gets the count of occurrences by status.
    /// </summary>
    protected Task<int> GetOccurrenceCountByStatusAsync(JobOccurrenceStatus status, CancellationToken cancellationToken = default)
    {
        var dbContext = GetDbContext();
        return dbContext.JobOccurrences.CountAsync(o => o.Status == status, cancellationToken);
    }

    /// <summary>
    /// Gets an occurrence by ID.
    /// </summary>
    protected Task<JobOccurrence> GetOccurrenceAsync(Guid occurrenceId, CancellationToken cancellationToken = default)
    {
        var dbContext = GetDbContext();
        return dbContext.JobOccurrences.AsNoTracking().Include(i => i.Logs).FirstOrDefaultAsync(o => o.Id == occurrenceId, cancellationToken);
    }

    /// <summary>
    /// Waits for a condition to be true with timeout.
    /// </summary>
    protected static async Task<bool> WaitForConditionAsync(
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
