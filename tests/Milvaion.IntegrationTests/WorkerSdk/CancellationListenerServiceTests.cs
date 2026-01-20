using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using Milvasoft.Milvaion.Sdk.Worker.RabbitMQ;
using StackExchange.Redis;
using System.Text.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.WorkerSdk;

/// <summary>
/// Integration tests for CancellationListenerService.
/// Tests job cancellation via Redis Pub/Sub.
/// </summary>
[Collection(nameof(MilvaionTestCollection))]
public class CancellationListenerServiceTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    private const string _cancellationChannel = "job:cancellation";

    [Fact]
    public async Task RegisterJob_ShouldTrackCancellationTokenSource()
    {
        // Arrange
        await InitializeAsync();

        var correlationId = Guid.CreateVersion7();
        using var cts = new CancellationTokenSource();

        // Act
        CancellationListenerService.RegisterJob(correlationId, cts);

        // Assert
        var token = CancellationListenerService.GetCancellationToken(correlationId);
        token.Should().NotBe(CancellationToken.None);
        token.CanBeCanceled.Should().BeTrue();

        // Cleanup
        CancellationListenerService.UnregisterJob(correlationId);
    }

    [Fact]
    public async Task UnregisterJob_ShouldRemoveTracking()
    {
        // Arrange
        await InitializeAsync();

        var correlationId = Guid.CreateVersion7();
        using var cts = new CancellationTokenSource();

        CancellationListenerService.RegisterJob(correlationId, cts);

        // Act
        CancellationListenerService.UnregisterJob(correlationId);

        // Assert
        var token = CancellationListenerService.GetCancellationToken(correlationId);
        token.Should().Be(CancellationToken.None);
    }

    [Fact]
    public async Task GetCancellationToken_WithUnregisteredJob_ShouldReturnNone()
    {
        // Arrange
        await InitializeAsync();

        var correlationId = Guid.CreateVersion7();

        // Act
        var token = CancellationListenerService.GetCancellationToken(correlationId);

        // Assert
        token.Should().Be(CancellationToken.None);
    }

    [Fact]
    public async Task CancellationListener_ShouldCancelJobOnPubSubMessage()
    {
        // Arrange
        await InitializeAsync();

        var correlationId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        using var cts = new CancellationTokenSource();
        using var serviceCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        CancellationListenerService.RegisterJob(correlationId, cts);

        // Start the listener service
        var service = CreateCancellationListenerService();
        _ = Task.Run(async () =>
        {
            try
            {
                await service.StartAsync(serviceCts.Token);
                await Task.Delay(Timeout.Infinite, serviceCts.Token);
            }
            catch (OperationCanceledException) { }
        }, serviceCts.Token);

        // Wait for service to start and subscribe
        await Task.Delay(2000, serviceCts.Token);

        // Act - Send cancellation request via Redis Pub/Sub
        await PublishCancellationRequestAsync(correlationId, jobId, "User requested cancellation");

        // Wait for cancellation to propagate
        var cancelled = await WaitForConditionAsync(
            () => Task.FromResult(cts.IsCancellationRequested),
            timeout: TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromMilliseconds(300),
            cancellationToken: serviceCts.Token);

        await service.StopAsync(serviceCts.Token);

        // Assert
        cancelled.Should().BeTrue("job should be cancelled via Redis Pub/Sub");
        cts.IsCancellationRequested.Should().BeTrue();

        // Cleanup
        CancellationListenerService.UnregisterJob(correlationId);
    }

    [Fact]
    public async Task CancellationListener_ShouldIgnoreUnregisteredJobs()
    {
        // Arrange
        await InitializeAsync();

        var correlationId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        using var serviceCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Note: Job is NOT registered

        // Start the listener service
        var service = CreateCancellationListenerService();
        _ = Task.Run(async () =>
        {
            try
            {
                await service.StartAsync(serviceCts.Token);
                await Task.Delay(Timeout.Infinite, serviceCts.Token);
            }
            catch (OperationCanceledException) { }
        }, serviceCts.Token);

        await Task.Delay(2000, serviceCts.Token);

        // Act - Send cancellation request for unregistered job
        await PublishCancellationRequestAsync(correlationId, jobId, "Cancellation for non-existent job");

        await Task.Delay(1000, serviceCts.Token);

        await service.StopAsync(serviceCts.Token);

        // Assert - No exception should be thrown, service should continue running
        var token = CancellationListenerService.GetCancellationToken(correlationId);
        token.Should().Be(CancellationToken.None);
    }

    [Fact]
    public async Task CancellationListener_ShouldHandleMultipleCancellations()
    {
        // Arrange
        await InitializeAsync();

        var jobs = new List<(Guid correlationId, CancellationTokenSource cts)>();
        for (int i = 0; i < 3; i++)
        {
            var correlationId = Guid.CreateVersion7();
            var cts = new CancellationTokenSource();
            CancellationListenerService.RegisterJob(correlationId, cts);
            jobs.Add((correlationId, cts));
        }

        using var serviceCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var service = CreateCancellationListenerService();
        _ = Task.Run(async () =>
        {
            try
            {
                await service.StartAsync(serviceCts.Token);
                await Task.Delay(Timeout.Infinite, serviceCts.Token);
            }
            catch (OperationCanceledException) { }
        }, serviceCts.Token);

        await Task.Delay(2000, serviceCts.Token);

        // Act - Cancel only the first and third jobs
        await PublishCancellationRequestAsync(jobs[0].correlationId, Guid.CreateVersion7(), "Cancel first");
        await PublishCancellationRequestAsync(jobs[2].correlationId, Guid.CreateVersion7(), "Cancel third");

        // Wait for cancellations to propagate
        var allCancelled = await WaitForConditionAsync(
            () => Task.FromResult(jobs[0].cts.IsCancellationRequested && jobs[2].cts.IsCancellationRequested),
            timeout: TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromMilliseconds(300),
            cancellationToken: serviceCts.Token);

        await service.StopAsync(serviceCts.Token);

        // Assert
        allCancelled.Should().BeTrue("first and third jobs should be cancelled");
        jobs[0].cts.IsCancellationRequested.Should().BeTrue();
        jobs[1].cts.IsCancellationRequested.Should().BeFalse(); // Second job not cancelled
        jobs[2].cts.IsCancellationRequested.Should().BeTrue();

        // Cleanup
        foreach (var (correlationId, cts) in jobs)
        {
            CancellationListenerService.UnregisterJob(correlationId);
            cts.Dispose();
        }
    }

    private CancellationListenerService CreateCancellationListenerService()
    {
        var redis = ConnectionMultiplexer.Connect(_factory.GetRedisConnectionString());

        var options = Options.Create(new WorkerOptions
        {
            WorkerId = $"test-worker-{Guid.NewGuid():N}",
            Redis = new RedisSettings
            {
                ConnectionString = _factory.GetRedisConnectionString(),
                CancellationChannel = _cancellationChannel
            }
        });

        return new CancellationListenerService(
            redis,
            options,
            _serviceProvider.GetRequiredService<ILoggerFactory>()
        );
    }

    private async Task PublishCancellationRequestAsync(Guid correlationId, Guid jobId, string reason)
    {
        var redis = await ConnectionMultiplexer.ConnectAsync(_factory.GetRedisConnectionString());
        var subscriber = redis.GetSubscriber();

        var request = new
        {
            CorrelationId = correlationId.ToString(),
            JobId = jobId.ToString(),
            Reason = reason
        };

        var message = JsonSerializer.Serialize(request);

        await subscriber.PublishAsync(RedisChannel.Literal(_cancellationChannel), message);
    }

    private static async Task<bool> WaitForConditionAsync(
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
