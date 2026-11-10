using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Utils;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using Milvasoft.Milvaion.Sdk.Worker.Persistence;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.WorkerSdk;

/// <summary>
/// Integration tests for ConnectionMonitor.
/// Tests RabbitMQ health checking against a real RabbitMQ instance.
/// </summary>
[Collection(nameof(WorkerSdkTestCollection))]
public class ConnectionMonitorTests(WorkerSdkContainerFixture fixture, ITestOutputHelper output) : WorkerSdkTestBase(fixture, output)
{
    [Fact]
    public void IsRabbitMQHealthy_ShouldBeOptimistic_BeforeFirstCheckCompletes()
    {
        // Arrange - A failed publish still falls back to the local store, so startup statuses are not held back
        var options = CreateWorkerOptions();

        using var monitor = new ConnectionMonitor(options, GetLogger());

        // Assert
        monitor.IsRabbitMQHealthy.Should().BeTrue();
    }

    [Fact]
    public async Task RefreshStatusAsync_ShouldReturnTrue_WhenRabbitMQIsReachable()
    {
        // Arrange
        var options = CreateWorkerOptions();

        using var monitor = new ConnectionMonitor(options, GetLogger());

        // Act
        var isHealthy = await monitor.RefreshStatusAsync();

        // Assert
        isHealthy.Should().BeTrue();
        monitor.IsRabbitMQHealthy.Should().BeTrue();
    }

    [Fact]
    public async Task RefreshStatusAsync_ShouldReturnFalse_WhenRabbitMQIsUnreachable()
    {
        // Arrange
        var options = new WorkerOptions
        {
            RabbitMQ = new RabbitMQSettings
            {
                Host = "invalid-host-that-does-not-exist",
                Port = 5672,
                Username = "guest",
                Password = "guest",
                VirtualHost = "/"
            }
        };

        using var monitor = new ConnectionMonitor(options, GetLogger());

        // Act
        var isHealthy = await monitor.RefreshStatusAsync();

        // Assert
        isHealthy.Should().BeFalse();
        monitor.IsRabbitMQHealthy.Should().BeFalse();
    }

    [Fact]
    public async Task IsRabbitMQHealthy_ShouldBecomeTrue_AfterBackgroundCheckCompletes()
    {
        // Arrange
        var options = CreateWorkerOptions();

        using var monitor = new ConnectionMonitor(options, GetLogger());

        // Act - Wait for background health check loop to complete at least once
        var healthy = false;

        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(500);

            if (monitor.IsRabbitMQHealthy)
            {
                healthy = true;
                break;
            }
        }

        // Assert
        healthy.Should().BeTrue("background health check should detect healthy RabbitMQ");
    }

    [Fact]
    public void OnConnectionRestored_ShouldNotThrow()
    {
        // Arrange
        var options = CreateWorkerOptions();

        using var monitor = new ConnectionMonitor(options, GetLogger());

        // Act & Assert
        var act = () => monitor.OnConnectionRestored();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_ShouldNotThrow_WhenCalledMultipleTimes()
    {
        // Arrange
        var options = CreateWorkerOptions();

        var monitor = new ConnectionMonitor(options, GetLogger());

        // Act & Assert
        var act = () =>
        {
            monitor.Dispose();
            monitor.Dispose();
        };

        act.Should().NotThrow();
    }

    [Fact]
    public async Task DisposeAsync_ShouldNotThrow_WhenCalledMultipleTimes()
    {
        // Arrange
        var options = CreateWorkerOptions();

        var monitor = new ConnectionMonitor(options, GetLogger());

        // Act & Assert
        var act = async () =>
        {
            await monitor.DisposeAsync();
            await monitor.DisposeAsync();
        };

        await act.Should().NotThrowAsync();
    }

    private WorkerOptions CreateWorkerOptions() => new()
    {
        RabbitMQ = new RabbitMQSettings
        {
            Host = GetRabbitMqHost(),
            Port = GetRabbitMqPort(),
            Username = "guest",
            Password = "guest",
            VirtualHost = "/"
        }
    };

    private IMilvaLogger GetLogger() => _serviceProvider.GetRequiredService<ILoggerFactory>().CreateMilvaLogger<ConnectionMonitor>();
}
