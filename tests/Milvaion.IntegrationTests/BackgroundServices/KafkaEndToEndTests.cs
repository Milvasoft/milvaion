using Confluent.Kafka;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Dtos.AdminDtos;
using Milvaion.Application.Features.ScheduledJobs.CancelJobOccurrence;
using Milvaion.Application.Features.ScheduledJobs.TriggerScheduledJob;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Interfaces.Messaging;
using Milvaion.Application.Utils.Constants;
using Milvaion.Infrastructure.Services.Kafka;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Utils;
using Milvasoft.Milvaion.Sdk.Utils.Kafka;
using Milvasoft.Milvaion.Sdk.Worker;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Milvasoft.Milvaion.Sdk.Worker.Kafka;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using Milvasoft.Milvaion.Sdk.Worker.Persistence;
using Milvasoft.Milvaion.Sdk.Worker.RabbitMQ;
using StackExchange.Redis;
using System.Net.Http.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.BackgroundServices;

/// <summary>
/// Runs jobs end to end on the Kafka transport: triggered through the API, dispatched to Kafka, executed by the .NET
/// worker, and reported back through the status, log and dead letter topics to the API's consumers.
/// </summary>
[Collection(nameof(KafkaTestCollection))]
[Trait("Kafka Integration Tests", "Jobs run end to end over Kafka.")]
public class KafkaEndToEndTests(KafkaWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    private const string _jobsUrl = $"{GlobalConstant.RoutePrefix}/v1.0/jobs";
    private static readonly TimeSpan _outcomeTimeout = TimeSpan.FromSeconds(90);

    private KafkaWebApplicationFactory KafkaFactory => (KafkaWebApplicationFactory)_factory;

    [Fact]
    public async Task Job_ShouldComplete_EndToEnd()
    {
        await using var worker = await StartWorkerAsync(new JobConsumerOptions { [nameof(KafkaSuccessJob)] = new JobConsumerConfig { ExecutionTimeoutSeconds = 30 } },
                                                        services => services.AddTransient<IJobBase, KafkaSuccessJob>());

        var job = await SeedJobAsync(worker.WorkerId, nameof(KafkaSuccessJob));
        var occurrenceId = await TriggerAsync(job.Id);

        var occurrence = await WaitForStatusAsync(occurrenceId, JobOccurrenceStatus.Completed);

        occurrence.Result.Should().Contain("done over kafka");

        // The log the job wrote came back through milvaion.logs.
        await WaitUntilAsync(async () => await GetDbContext().JobOccurrenceLogs.AnyAsync(l => l.OccurrenceId == occurrenceId && l.Message.Contains("hello from kafka")), "the job's log to be stored");

        // The record was committed once its outcome was reported.
        await WaitUntilAsync(() => Task.FromResult(GetCommittedOffset(worker.Clients.Group, worker.Clients.JobsTopic) >= 1), "the job record to be committed");
    }

    [Fact]
    public async Task FailingJob_ShouldRetryThroughTheDelayTopic_ThenLandInTheDeadLetterTopic()
    {
        await using var worker = await StartWorkerAsync(new JobConsumerOptions
        {
            [nameof(KafkaFailingJob)] = new JobConsumerConfig { ExecutionTimeoutSeconds = 30, MaxRetries = 2, BaseRetryDelaySeconds = 1 }
        }, services => services.AddTransient<IJobBase, KafkaFailingJob>());

        var job = await SeedJobAsync(worker.WorkerId, nameof(KafkaFailingJob));
        var occurrenceId = await TriggerAsync(job.Id);

        var occurrence = await WaitForStatusAsync(occurrenceId, JobOccurrenceStatus.Failed);

        occurrence.Exception.Should().Contain("Max retries (2) exceeded");
        KafkaFailingJob.Attempts(occurrenceId).Should().Be(3, "the first attempt plus two retries");

        // The failed occurrence handler created the record from milvaion.dlq.
        await WaitUntilAsync(async () => await GetDbContext().FailedOccurrences.AnyAsync(f => f.OccurrenceId == occurrenceId), "the failed occurrence record");

        var failed = await GetDbContext().FailedOccurrences.SingleAsync(f => f.OccurrenceId == occurrenceId);

        failed.RetryCount.Should().Be(2);
        failed.Exception.Should().Contain("Simulated Kafka failure");

        // Both retries went through the retry topic, and every record was committed.
        await WaitUntilAsync(() => Task.FromResult(GetCommittedOffset(worker.Clients.Group, worker.Clients.RetryTopic) >= 2), "both retry records to be committed");
    }

    [Fact]
    public async Task SlowJob_ShouldTimeOut()
    {
        await using var worker = await StartWorkerAsync(new JobConsumerOptions { [nameof(KafkaSlowJob)] = new JobConsumerConfig { ExecutionTimeoutSeconds = 1 } },
                                                        services => services.AddTransient<IJobBase, KafkaSlowJob>());

        var job = await SeedJobAsync(worker.WorkerId, nameof(KafkaSlowJob));
        var occurrenceId = await TriggerAsync(job.Id);

        var occurrence = await WaitForStatusAsync(occurrenceId, JobOccurrenceStatus.TimedOut);

        occurrence.Exception.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RunningJob_ShouldBeCancelled_FromTheApi()
    {
        await using var worker = await StartWorkerAsync(new JobConsumerOptions { [nameof(KafkaSlowJob)] = new JobConsumerConfig { ExecutionTimeoutSeconds = 120 } },
                                                        services => services.AddTransient<IJobBase, KafkaSlowJob>());

        var job = await SeedJobAsync(worker.WorkerId, nameof(KafkaSlowJob));
        var occurrenceId = await TriggerAsync(job.Id);

        await WaitForStatusAsync(occurrenceId, JobOccurrenceStatus.Running);

        var client = await _factory.CreateClient().LoginAsync();
        var response = await client.PostAsJsonAsync($"{_jobsUrl}/occurrences/cancel", new CancelJobOccurrenceCommand { OccurrenceId = occurrenceId, Reason = "Kafka test" });
        var result = await response.Content.ReadFromJsonAsync<Response<bool>>();

        result.IsSuccess.Should().BeTrue(result.Messages?.FirstOrDefault()?.Message);

        await WaitForStatusAsync(occurrenceId, JobOccurrenceStatus.Cancelled);
    }

    [Fact]
    public async Task QueueStatistics_ShouldReportKafkaTopicsWithConsumerLag()
    {
        await using var worker = await StartWorkerAsync(new JobConsumerOptions { [nameof(KafkaSuccessJob)] = new JobConsumerConfig() },
                                                        services => services.AddTransient<IJobBase, KafkaSuccessJob>());

        var stats = await _serviceProvider.GetRequiredService<IQueueDepthMonitor>().GetAllQueueStatsAsync();

        stats.Should().Contain(s => s.QueueName == KafkaTopicNames.Status);
        stats.Should().Contain(s => s.QueueName == worker.Clients.JobsTopic);
        stats.Should().OnlyContain(s => s.Transport == "Kafka" && s.MessagesUnacknowledged == null);
        stats.Single(s => s.QueueName == KafkaTopicNames.Status).HealthStatus.Should().NotBe(Application.Utils.Enums.QueueHealthStatus.Unavailable);

        var health = await _serviceProvider.GetRequiredService<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService>().CheckHealthAsync();

        health.Entries.Should().ContainKey("Kafka");
        health.Entries["Kafka"].Status.Should().Be(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy);
        health.Entries.Should().NotContainKey("RabbitMQ");
    }

    [Fact]
    public async Task Publisher_ShouldReportUnroutable_WhenTheWorkerTopicDoesNotExist()
    {
        var job = new ScheduledJob { Id = Guid.CreateVersion7(), JobNameInWorker = "Nothing", WorkerId = $"missing-{Guid.NewGuid():N}" };

        var published = await _serviceProvider.GetRequiredService<IJobPublisher>().PublishJobAsync(job, Guid.CreateVersion7());

        published.Should().BeFalse();
    }

    #region Helpers

    private async Task<ScheduledJob> SeedJobAsync(string workerId, string jobName)
    {
        await SeedRootUserAndSuperAdminRoleAsync();

        var dbContext = GetDbContext();

        var job = new ScheduledJob
        {
            Id = Guid.CreateVersion7(),
            DisplayName = $"Kafka {jobName}",
            JobNameInWorker = jobName,
            WorkerId = workerId,
            ExecuteAt = DateTime.UtcNow.AddHours(1),
            IsActive = true,
            CreationDate = DateTime.UtcNow,
            CreatorUserName = GlobalConstant.SystemUsername
        };

        await dbContext.ScheduledJobs.AddAsync(job);
        await dbContext.SaveChangesAsync();

        return job;
    }

    private async Task<Guid> TriggerAsync(Guid jobId)
    {
        var client = await _factory.CreateClient().LoginAsync();
        var response = await client.PostAsJsonAsync($"{_jobsUrl}/job/trigger", new TriggerScheduledJobCommand { JobId = jobId, Reason = "Kafka end to end" });
        var result = await response.Content.ReadFromJsonAsync<Response<Guid>>();

        result.IsSuccess.Should().BeTrue(result.Messages?.FirstOrDefault()?.Message);

        return result.Data;
    }

    private async Task<JobOccurrence> WaitForStatusAsync(Guid occurrenceId, JobOccurrenceStatus status)
    {
        JobOccurrence occurrence = null;

        await WaitUntilAsync(async () =>
        {
            occurrence = await GetDbContext().JobOccurrences.AsNoTracking().FirstOrDefaultAsync(o => o.Id == occurrenceId);

            return occurrence?.Status == status;
        }, $"occurrence {occurrenceId} to reach {status} (last seen: {occurrence?.Status})");

        return occurrence;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + _outcomeTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;

            await Task.Delay(250);
        }

        (await condition()).Should().BeTrue($"timed out waiting for {what}");
    }

    private long GetCommittedOffset(string group, string topic)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = KafkaFactory.GetKafkaBootstrapServers() }).Build();

        var partitions = admin.GetMetadata(topic, TimeSpan.FromSeconds(10)).Topics.Single().Partitions.Select(p => new TopicPartition(topic, p.PartitionId)).ToList();
        var offsets = admin.ListConsumerGroupOffsetsAsync([new ConsumerGroupTopicPartitions(group, partitions)]).GetAwaiter().GetResult();

        return offsets.SelectMany(o => o.Partitions).Where(p => p.Offset.Value > 0).Sum(p => p.Offset.Value);
    }

    /// <summary>
    /// Starts a .NET worker on Kafka with its own worker id, so its topics and group are not shared with other tests.
    /// </summary>
    private async Task<RunningWorker> StartWorkerAsync(JobConsumerOptions jobConsumers, Action<IServiceCollection> registerJobs)
    {
        var workerId = $"kafka-e2e-{Guid.NewGuid():N}"[..24];

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Worker:WorkerId"] = workerId,
            ["Worker:MaxParallelJobs"] = "4",
            ["Worker:Transport:Type"] = "Kafka",
            ["Worker:Kafka:BootstrapServers"] = KafkaFactory.GetKafkaBootstrapServers(),
            ["Worker:Kafka:NumPartitions"] = "3",
            ["Worker:Kafka:ReplicationFactor"] = "1",
            ["Worker:Redis:ConnectionString"] = _factory.GetRedisConnectionString(),
            ["Worker:OfflineResilience:Enabled"] = "false",
            ["Worker:Heartbeat:JobHeartbeatIntervalSeconds"] = "5",
        }).Build();

        var services = new ServiceCollection();

        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Information));
        services.AddMilvaionWorkerCore(configuration, requireJobConsumers: true);
        registerJobs(services);

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<WorkerOptions>>();
        var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
        var clients = provider.GetRequiredService<KafkaWorkerClients>();

        // The topics have to exist before the API dispatches to them.
        await clients.EnsureWorkerTopicsAsync();

        var cancellationListener = new CancellationListenerService(provider.GetRequiredService<IConnectionMultiplexer>(), options, loggerFactory);
        var consumer = new KafkaJobConsumer(provider,
                                            options,
                                            Options.Create(jobConsumers),
                                            loggerFactory.CreateMilvaLogger<KafkaJobConsumer>(),
                                            clients,
                                            provider.GetRequiredService<OutboxService>());

        await cancellationListener.StartAsync(CancellationToken.None);
        await consumer.StartAsync(CancellationToken.None);

        return new RunningWorker(workerId, provider, clients, consumer, cancellationListener);
    }

    private sealed class RunningWorker(string workerId, ServiceProvider provider, KafkaWorkerClients clients, KafkaJobConsumer consumer, CancellationListenerService cancellationListener) : IAsyncDisposable
    {
        public string WorkerId { get; } = workerId;

        public KafkaWorkerClients Clients { get; } = clients;

        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            await consumer.StopAsync(timeout.Token);
            await cancellationListener.StopAsync(timeout.Token);

            consumer.Dispose();

            await provider.DisposeAsync();
        }
    }

    #endregion

    #region Jobs

    private sealed class KafkaSuccessJob : IAsyncJobWithResult<string>
    {
        public Task<string> ExecuteAsync(IJobContext context)
        {
            context.LogInformation("hello from kafka");

            return Task.FromResult("done over kafka");
        }
    }

    private sealed class KafkaFailingJob : IAsyncJob
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _attempts = new();

        public static int Attempts(Guid occurrenceId) => _attempts.GetValueOrDefault(occurrenceId);

        public Task ExecuteAsync(IJobContext context)
        {
            _attempts.AddOrUpdate(context.OccurrenceId, 1, (_, count) => count + 1);

            throw new InvalidOperationException("Simulated Kafka failure");
        }
    }

    private sealed class KafkaSlowJob : IAsyncJob
    {
        public Task ExecuteAsync(IJobContext context) => Task.Delay(TimeSpan.FromMinutes(5), context.CancellationToken);
    }

    #endregion
}
