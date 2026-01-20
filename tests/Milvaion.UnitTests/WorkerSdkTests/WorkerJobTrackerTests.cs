using FluentAssertions;
using Microsoft.Extensions.Logging;
using Milvasoft.Milvaion.Sdk.Worker.Core;
using Moq;

namespace Milvaion.UnitTests.WorkerSdkTests;

[Trait("SDK Unit Tests", "WorkerJobTracker unit tests.")]
public class WorkerJobTrackerTests
{
    private readonly Mock<ILoggerFactory> _loggerFactoryMock;
    private readonly WorkerJobTracker _tracker;

    public WorkerJobTrackerTests()
    {
        _loggerFactoryMock = new Mock<ILoggerFactory>();
        _loggerFactoryMock.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

        _tracker = new WorkerJobTracker(_loggerFactoryMock.Object);
    }

    [Fact]
    public void GetJobCount_ShouldReturnZero_WhenWorkerNotExists()
    {
        // Act
        var count = _tracker.GetJobCount("non-existent-worker");

        // Assert
        count.Should().Be(0);
    }

    [Fact]
    public void IncrementJobCount_ShouldIncrementCount_WhenCalled()
    {
        // Arrange
        var workerId = "test-worker";

        // Act
        _tracker.IncrementJobCount(workerId);

        // Assert
        _tracker.GetJobCount(workerId).Should().Be(1);
    }

    [Fact]
    public void IncrementJobCount_ShouldIncrementMultipleTimes()
    {
        // Arrange
        var workerId = "test-worker";

        // Act
        _tracker.IncrementJobCount(workerId);
        _tracker.IncrementJobCount(workerId);
        _tracker.IncrementJobCount(workerId);

        // Assert
        _tracker.GetJobCount(workerId).Should().Be(3);
    }

    [Fact]
    public void DecrementJobCount_ShouldDecrementCount_WhenCalled()
    {
        // Arrange
        var workerId = "test-worker";
        _tracker.IncrementJobCount(workerId);
        _tracker.IncrementJobCount(workerId);

        // Act
        _tracker.DecrementJobCount(workerId);

        // Assert
        _tracker.GetJobCount(workerId).Should().Be(1);
    }

    [Fact]
    public void DecrementJobCount_ShouldNotGoBelowZero()
    {
        // Arrange
        var workerId = "test-worker";

        // Act
        _tracker.DecrementJobCount(workerId);
        _tracker.DecrementJobCount(workerId);

        // Assert
        _tracker.GetJobCount(workerId).Should().Be(0);
    }

    [Fact]
    public void GetAllJobCounts_ShouldReturnEmptyDictionary_WhenNoWorkers()
    {
        // Act
        var counts = _tracker.GetAllJobCounts();

        // Assert
        counts.Should().BeEmpty();
    }

    [Fact]
    public void GetAllJobCounts_ShouldReturnAllWorkerCounts()
    {
        // Arrange
        _tracker.IncrementJobCount("worker-1");
        _tracker.IncrementJobCount("worker-1");
        _tracker.IncrementJobCount("worker-2");
        _tracker.IncrementJobCount("worker-3");
        _tracker.IncrementJobCount("worker-3");
        _tracker.IncrementJobCount("worker-3");

        // Act
        var counts = _tracker.GetAllJobCounts();

        // Assert
        counts.Should().HaveCount(3);
        counts["worker-1"].Should().Be(2);
        counts["worker-2"].Should().Be(1);
        counts["worker-3"].Should().Be(3);
    }

    [Fact]
    public void GetAllJobCounts_ShouldReturnNewDictionary()
    {
        // Arrange
        _tracker.IncrementJobCount("worker-1");

        // Act
        var counts1 = _tracker.GetAllJobCounts();
        var counts2 = _tracker.GetAllJobCounts();

        // Assert
        counts1.Should().NotBeSameAs(counts2);
    }

    [Fact]
    public void IncrementAndDecrement_ShouldWorkCorrectly_ForMultipleWorkers()
    {
        // Arrange & Act
        _tracker.IncrementJobCount("worker-1");
        _tracker.IncrementJobCount("worker-2");
        _tracker.IncrementJobCount("worker-1");
        _tracker.DecrementJobCount("worker-1");
        _tracker.IncrementJobCount("worker-2");
        _tracker.DecrementJobCount("worker-2");

        // Assert
        _tracker.GetJobCount("worker-1").Should().Be(1);
        _tracker.GetJobCount("worker-2").Should().Be(1);
    }

    [Fact]
    public async Task IncrementJobCount_ShouldBeThreadSafe()
    {
        // Arrange
        var workerId = "concurrent-worker";
        var tasks = new List<Task>();

        // Act - Increment from multiple threads concurrently
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() => _tracker.IncrementJobCount(workerId)));
        }

        await Task.WhenAll(tasks);

        // Assert
        _tracker.GetJobCount(workerId).Should().Be(100);
    }

    [Fact]
    public async Task DecrementJobCount_ShouldBeThreadSafe()
    {
        // Arrange
        var workerId = "concurrent-worker";

        // First, increment 100 times
        for (int i = 0; i < 100; i++)
        {
            _tracker.IncrementJobCount(workerId);
        }

        var tasks = new List<Task>();

        // Act - Decrement from multiple threads concurrently
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() => _tracker.DecrementJobCount(workerId)));
        }

        await Task.WhenAll(tasks);

        // Assert
        _tracker.GetJobCount(workerId).Should().Be(0);
    }

    [Fact]
    public async Task MixedOperations_ShouldBeThreadSafe()
    {
        // Arrange
        var workerId = "mixed-worker";
        var incrementTasks = new List<Task>();
        var decrementTasks = new List<Task>();

        // Pre-increment to ensure we have enough to decrement
        for (int i = 0; i < 50; i++)
        {
            _tracker.IncrementJobCount(workerId);
        }

        // Act - Mix increments and decrements concurrently
        for (int i = 0; i < 50; i++)
        {
            incrementTasks.Add(Task.Run(() => _tracker.IncrementJobCount(workerId)));
            decrementTasks.Add(Task.Run(() => _tracker.DecrementJobCount(workerId)));
        }

        await Task.WhenAll(incrementTasks);
        await Task.WhenAll(decrementTasks);

        // Assert - Should be 50 (50 initial + 50 increments - 50 decrements)
        _tracker.GetJobCount(workerId).Should().Be(50);
    }
}
