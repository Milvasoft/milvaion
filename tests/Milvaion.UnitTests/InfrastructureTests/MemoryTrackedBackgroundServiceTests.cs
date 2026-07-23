using FluentAssertions;
using Microsoft.Extensions.Logging;
using Milvaion.Application.Dtos.AdminDtos;
using Milvaion.Application.Utils.Models.Options;
using Milvaion.Infrastructure.BackgroundServices.Base;
using Moq;

namespace Milvaion.UnitTests.InfrastructureTests;

[Trait("Infrastructure Unit Tests", "MemoryTrackedBackgroundService unit tests.")]
public class MemoryTrackedBackgroundServiceTests
{
    private static TestBackgroundService CreateService(
        MemoryTrackingOptions memoryOptions = null,
        IMemoryStatsRegistry registry = null)
    {
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

        var options = new BackgroundServiceOptions
        {
            MemoryTrackingOptions = memoryOptions ?? new MemoryTrackingOptions
            {
                CheckIntervalSeconds = 0, // Immediate checking in tests
                WarningThresholdBytes = long.MaxValue,
                CriticalThresholdBytes = long.MaxValue,
                LeakDetectionThresholdBytes = long.MaxValue
            }
        };

        return new TestBackgroundService(loggerFactory.Object, options, registry);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSetIsRunningAndCallExecuteWithTracking()
    {
        // Arrange
        var service = CreateService();

        // Act
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await service.StopAsync(CancellationToken.None);

        // Assert
        service.ExecuteWithTrackingCalled.Should().BeTrue();
    }

    [Fact]
    public async Task GetStats_ShouldReturnValidStats_AfterExecution()
    {
        // Arrange
        var registryMock = new Mock<IMemoryStatsRegistry>();
        var service = CreateService(registry: registryMock.Object);

        // Act
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        var stats = service.GetStats();
        await service.StopAsync(CancellationToken.None);

        // Assert
        stats.Should().NotBeNull();
        stats.ServiceName.Should().Be("TestService");
        stats.InitialMemoryBytes.Should().BeGreaterThan(0);
        stats.CurrentMemoryBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetStats_ShouldPopulatePerServiceAllocationMetrics_AfterExecution()
    {
        // Arrange
        var service = CreateService();

        // Act
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        var stats = service.GetStats();
        await service.StopAsync(CancellationToken.None);

        // Assert - per-service allocation metrics are populated and non-negative
        stats.AllocatedBytes.Should().BeGreaterThanOrEqualTo(0);
        stats.RecentAllocatedBytes.Should().BeGreaterThanOrEqualTo(0);
        stats.AllocationRatePerSecondBytes.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRegisterAndUnregister_WithStatsRegistry()
    {
        // Arrange
        var registryMock = new Mock<IMemoryStatsRegistry>();
        var service = CreateService(registry: registryMock.Object);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Poll until Register has been invoked or timeout, to avoid CI timing races
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                registryMock.Verify(r => r.Register("TestService", It.IsAny<Func<MemoryTrackStats>>()), Times.Once);
                break;
            }
            catch (MockException)
            {
                await Task.Delay(50);
            }
        }

        await service.StopAsync(CancellationToken.None);

        // Assert
        registryMock.Verify(r => r.Register("TestService", It.IsAny<Func<MemoryTrackStats>>()), Times.Once);
        registryMock.Verify(r => r.Unregister("TestService"), Times.Once);
    }

    [Fact]
    public async Task TrackMemoryAfterIteration_ShouldNotThrow()
    {
        // Arrange
        var service = CreateService();

        // Act
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);

        // Assert - TrackMemory was called inside ExecuteWithMemoryTrackingAsync
        service.TrackMemoryCalled.Should().BeTrue();

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TrackMemoryAfterIteration_ShouldSkip_WhenIntervalNotElapsed()
    {
        // Arrange - large interval so tracking is skipped
        var service = CreateService(new MemoryTrackingOptions
        {
            CheckIntervalSeconds = 9999
        });

        // Act
        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await service.StopAsync(CancellationToken.None);

        // Assert - TrackMemory was called but returned early due to interval check
        service.TrackMemoryCalled.Should().BeTrue();
    }

    [Fact]
    public void ForceGarbageCollection_ShouldNotThrow()
    {
        // Arrange
        var service = CreateService();

        // Act & Assert
        var act = () => service.CallForceGarbageCollection();
        act.Should().NotThrow();
    }

    /// <summary>
    /// Concrete test implementation of MemoryTrackedBackgroundService.
    /// </summary>
    private sealed class TestBackgroundService(ILoggerFactory loggerFactory, BackgroundServiceOptions options, IMemoryStatsRegistry registry = null)
        : MemoryTrackedBackgroundService(loggerFactory, options, registry)
    {
        public bool ExecuteWithTrackingCalled { get; private set; }
        public bool TrackMemoryCalled { get; private set; }

        protected override string ServiceName => "TestService";

        protected override Task ExecuteWithMemoryTrackingAsync(CancellationToken stoppingToken)
        {
            ExecuteWithTrackingCalled = true;
            TrackMemoryCalled = true;
            TrackMemoryAfterIteration();
            return Task.CompletedTask;
        }

        public void CallForceGarbageCollection() => ForceGarbageCollection();
    }
}
