using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Milvaion.Application.Interfaces.RabbitMQ;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Milvaion.Sdk.Utils;
using RabbitMQ.Client;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.BackgroundServices;

/// <summary>
/// Integration tests for <see cref="IRabbitMQPublisher"/>.
/// Verifies mandatory-return detection: a job publish must report failure when no worker
/// queue is bound to its routing key (worker offline), and success when a queue is bound.
/// </summary>
[Collection(nameof(ServicesTestCollection))]
public class RabbitMQPublisherTests(ServicesWebApplicationFactory factory, ITestOutputHelper output) : BackgroundServiceTestBase(factory, output)
{
    private const string _catchAllQueueName = "test-job-dispatch-queue";

    [Fact]
    public async Task PublishJobAsync_WhenWorkerQueueBound_ShouldReturnTrue()
    {
        // Arrange — InitializeAsync declares the catch-all queue bound to "#",
        // so any routing key on the jobs exchange is routable.
        await InitializeAsync();

        var job = await SeedScheduledJobAsync($"RoutableJob_{Guid.CreateVersion7():N}", workerId: $"w-{Guid.CreateVersion7():N}");
        var publisher = _serviceProvider.GetRequiredService<IRabbitMQPublisher>();

        // Act
        var published = await publisher.PublishJobAsync(job, Guid.CreateVersion7());

        // Assert
        published.Should().BeTrue("a bound catch-all queue makes the mandatory publish routable");
    }

    [Fact]
    public async Task PublishJobAsync_WhenNoWorkerQueueBound_ShouldReturnFalse()
    {
        // Arrange
        await InitializeAsync();

        // Remove the catch-all queue so nothing is bound to the job's unique routing key,
        // simulating an offline worker. The exchange itself is kept declared.
        await RemoveCatchAllQueueAndEnsureExchangeAsync();

        var job = await SeedScheduledJobAsync($"UnroutableJob_{Guid.CreateVersion7():N}", workerId: $"offline-{Guid.CreateVersion7():N}");
        var publisher = _serviceProvider.GetRequiredService<IRabbitMQPublisher>();

        // Act
        var published = await publisher.PublishJobAsync(job, Guid.CreateVersion7());

        // Assert
        published.Should().BeFalse("an unroutable mandatory publish (no bound worker queue) must be reported as failed");
    }

    private async Task RemoveCatchAllQueueAndEnsureExchangeAsync()
    {
        var rabbitFactory = new ConnectionFactory
        {
            HostName = _factory.GetRabbitMqHost(),
            Port = _factory.GetRabbitMqPort(),
            UserName = "guest",
            Password = "guest"
        };

        await using var connection = await rabbitFactory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        // Keep the exchange so the publish targets a real (but unbound) exchange.
        await channel.ExchangeDeclareAsync(
            exchange: WorkerConstant.ExchangeName,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false);

        await channel.QueueDeleteAsync(_catchAllQueueName);
    }
}
