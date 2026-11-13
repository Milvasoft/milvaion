using Milvaion.Application.Interfaces.Messaging;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Interfaces.RabbitMQ;
using Milvaion.Application.Interfaces.Redis;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.Models.Options;
using Milvaion.Infrastructure.BackgroundServices;
using Milvaion.Infrastructure.Services.RabbitMQ;
using Milvaion.Infrastructure.Services.Secrets;
using Milvaion.Infrastructure.Telemetry;
using Milvaion.IntegrationTests.ControllersTests;
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
/// Secret references through the scheduled dispatch path and the dead letter path.
/// </summary>
[Collection(nameof(ServicesTestCollection))]
public class SecretReferenceDispatchTests(ServicesWebApplicationFactory factory, ITestOutputHelper output) : BackgroundServiceTestBase(factory, output)
{
    private static string Ref(string name) => "{{secret:" + name + "}}";

    [Fact]
    public async Task Dispatcher_WithMissingSecret_ShouldFailTheOccurrenceWithoutPublishing()
    {
        // Arrange
        await InitializeAsync();

        var secretName = $"missing-{Guid.NewGuid():N}";
        // No worker id: an unregistered worker fails the capacity check. A unique routing pattern keeps other tests' messages out of the queue.
        var job = await SeedScheduledJobAsync($"SecretDispatch_{Guid.CreateVersion7():N}", executeAt: DateTime.UtcNow.AddMinutes(-1));

        await SetJobDataAsync(job, $$"""{"password":"{{Ref(secretName)}}"}""", routingPattern: $"secretit{Guid.NewGuid():N}.*");

        await using var rabbit = await RabbitQueue.BindAsync(_factory, job);

        var redisScheduler = _serviceProvider.GetRequiredService<IRedisSchedulerService>();
        await redisScheduler.AddToScheduledSetAsync(job.Id, job.ExecuteAt);
        await redisScheduler.CacheJobDetailsAsync(job, TimeSpan.FromHours(1));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act
        var dispatcher = CreateJobDispatcherService();
        await dispatcher.StartAsync(cts.Token);

        var failed = await WaitForConditionAsync(async () =>
        {
            var occurrence = await GetDbContext().JobOccurrences.AsNoTracking().FirstOrDefaultAsync(o => o.JobId == job.Id, cts.Token);
            return occurrence?.Status == JobOccurrenceStatus.Failed;
        }, timeout: TimeSpan.FromSeconds(20), pollInterval: TimeSpan.FromMilliseconds(300), cancellationToken: cts.Token);

        await dispatcher.StopAsync(cts.Token);

        // Assert
        failed.Should().BeTrue("a missing secret fails the occurrence instead of retrying it");

        var stored = await GetDbContext().JobOccurrences.AsNoTracking().Include(o => o.Logs).FirstAsync(o => o.JobId == job.Id, cts.Token);
        stored.Exception.Should().Contain(secretName).And.Contain("not published");
        stored.NextDispatchRetryAt.Should().BeNull();
        stored.Logs.Should().Contain(l => l.Level == "Error" && l.Message.Contains(secretName));

        (await rabbit.GetAsync()).Should().BeNull("nothing may be published when a secret is missing");
    }

    [Fact]
    public async Task FailedOccurrence_FromDeadLetteredResolvedJob_ShouldStoreTheTemplateAndMaskTheException()
    {
        // Arrange
        await InitializeAsync();

        var secretName = $"dlq-{Guid.NewGuid():N}";
        var secretValue = $"dlq-value-{Guid.NewGuid():N}";
        var variable = EnvironmentSecretProvider.ToVariableName("MILVAION_SECRET_", secretName);
        var template = $$"""{"password":"{{Ref(secretName)}}"}""";

        Environment.SetEnvironmentVariable(variable, secretValue);

        try
        {
            var job = await SeedScheduledJobAsync($"SecretDlq_{Guid.CreateVersion7():N}", workerId: $"w{Guid.NewGuid():N}");
            await SetJobDataAsync(job, template);

            var occurrence = await SeedJobOccurrenceAsync(job.Id, job.JobNameInWorker, JobOccurrenceStatus.Failed);

            await using var rabbit = await RabbitQueue.BindAsync(_factory, job);

            // Dispatch through the real publisher, as the dispatcher does, and take the message the worker gets.
            var published = await _serviceProvider.GetRequiredService<IRabbitMQPublisher>().PublishJobAsync(job, occurrence.CorrelationId);
            published.Should().BeTrue();

            var workerCopy = JsonDocument.Parse(await rabbit.GetAsync()).RootElement.GetProperty("jobData").GetString();
            workerCopy.Should().Contain(secretValue);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            var handler = new FailedOccurrenceHandler(_serviceProvider,
                                                      _serviceProvider.GetRequiredService<IMessageConsumerTransport>(),
                                                      _serviceProvider.GetRequiredService<IAlertNotifier>(),
                                                      Options.Create(new FailedOccurrenceHandlerOptions { Enabled = true }),
                                                      _serviceProvider.GetRequiredService<ILoggerFactory>(),
                                                      _serviceProvider.GetRequiredService<BackgroundServiceMetrics>());

            _ = Task.Run(async () =>
            {
                try
                {
                    await handler.StartAsync(cts.Token);
                    await Task.Delay(Timeout.Infinite, cts.Token);
                }
                catch (OperationCanceledException) { }
            }, cts.Token);

            await Task.Delay(1000, cts.Token);

            // Act - the worker gives up and dead-letters the job exactly as it received it.
            await PublishToDlqAsync(job, occurrence, workerCopy, $"Login rejected for password {secretValue}", cts.Token);

            var stored = await WaitForConditionAsync(async () =>
                await GetDbContext().FailedOccurrences.AsNoTracking().AnyAsync(f => f.OccurrenceId == occurrence.Id, cts.Token),
                timeout: TimeSpan.FromSeconds(15), pollInterval: TimeSpan.FromMilliseconds(300), cancellationToken: cts.Token);

            await handler.StopAsync(cts.Token);
            await cts.CancelAsync();

            // Assert
            stored.Should().BeTrue();

            var failed = await GetDbContext().FailedOccurrences.AsNoTracking().SingleAsync(f => f.OccurrenceId == occurrence.Id);

            failed.JobData.Should().Contain($"secret:{secretName}").And.NotContain(secretValue);
            failed.Exception.Should().Contain(Ref(secretName)).And.NotContain(secretValue);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    private async Task SetJobDataAsync(ScheduledJob job, string jobData, string routingPattern = null)
    {
        job.JobData = jobData;
        job.RoutingPattern = routingPattern ?? job.RoutingPattern;

        await GetDbContext().ScheduledJobs.Where(j => j.Id == job.Id)
                            .ExecuteUpdateAsync(s => s.SetProperty(j => j.JobData, jobData).SetProperty(j => j.RoutingPattern, job.RoutingPattern));
    }

    private async Task PublishToDlqAsync(ScheduledJob job, JobOccurrence occurrence, string jobData, string exception, CancellationToken cancellationToken)
    {
        var connection = await new ConnectionFactory
        {
            HostName = _factory.GetRabbitMqHost(),
            Port = _factory.GetRabbitMqPort(),
            UserName = "guest",
            Password = "guest"
        }.CreateConnectionAsync(cancellationToken);

        await using (connection)
        {
            await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

            await channel.QueueDeclareAsync(WorkerConstant.Queues.FailedOccurrences, durable: true, exclusive: false, autoDelete: false, arguments: null, cancellationToken: cancellationToken);

            var message = new DlqJobMessage
            {
                Id = job.Id,
                DisplayName = job.DisplayName,
                JobNameInWorker = job.JobNameInWorker,
                JobData = jobData,
                Status = JobOccurrenceStatus.Failed,
                Exception = exception
            };

            var properties = new BasicProperties
            {
                Persistent = true,
                CorrelationId = occurrence.CorrelationId.ToString(),
                Headers = new Dictionary<string, object>
                {
                    ["CorrelationId"] = Encoding.UTF8.GetBytes(occurrence.CorrelationId.ToString()),
                    ["x-retry-count"] = 3,
                    ["MaxRetries"] = 3
                }
            };

            await channel.BasicPublishAsync(exchange: "",
                                            routingKey: WorkerConstant.Queues.FailedOccurrences,
                                            mandatory: false,
                                            basicProperties: properties,
                                            body: Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, ConstantJsonOptions.PropNameCaseInsensitive)),
                                            cancellationToken: cancellationToken);
        }
    }

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
                EnableStartupRecovery = false
            }),
            _serviceProvider.GetRequiredService<IDispatcherControlService>(),
            _serviceProvider.GetRequiredService<ILoggerFactory>(),
            _serviceProvider.GetRequiredService<BackgroundServiceMetrics>());
}
