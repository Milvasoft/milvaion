using FluentAssertions;
using Milvaion.Infrastructure.Services.Redis.Utils;

namespace Milvaion.UnitTests.InfrastructureTests;

[Trait("Infrastructure Unit Tests", "CircuitBreakerStats unit tests.")]
public class CircuitBreakerStatsTests
{
    [Fact]
    public void CircuitBreakerStats_ShouldInitializeWithDefaultValues()
    {
        // Act
        var stats = new CircuitBreakerStats();

        // Assert
        stats.State.Should().Be(CircuitState.Closed);
        stats.FailureCount.Should().Be(0);
        stats.LastFailureTime.Should().BeNull();
        stats.TotalOperations.Should().Be(0);
        stats.TotalFailures.Should().Be(0);
        stats.StatsResetTime.Should().Be(default);
        stats.SuccessRate.Should().Be(0);
    }

    [Fact]
    public void SuccessRate_ShouldReturnZero_WhenNoOperations()
    {
        // Arrange
        var stats = new CircuitBreakerStats
        {
            TotalOperations = 0,
            TotalFailures = 0
        };

        // Act
        var successRate = stats.SuccessRate;

        // Assert
        successRate.Should().Be(0);
    }

    [Fact]
    public void SuccessRate_ShouldReturnOne_WhenAllOperationsSucceed()
    {
        // Arrange
        var stats = new CircuitBreakerStats
        {
            TotalOperations = 100,
            TotalFailures = 0
        };

        // Act
        var successRate = stats.SuccessRate;

        // Assert
        successRate.Should().Be(1.0);
    }

    [Fact]
    public void SuccessRate_ShouldReturnZero_WhenAllOperationsFail()
    {
        // Arrange
        var stats = new CircuitBreakerStats
        {
            TotalOperations = 100,
            TotalFailures = 100
        };

        // Act
        var successRate = stats.SuccessRate;

        // Assert
        successRate.Should().Be(0);
    }

    [Fact]
    public void SuccessRate_ShouldCalculateCorrectly_WhenPartialFailures()
    {
        // Arrange
        var stats = new CircuitBreakerStats
        {
            TotalOperations = 100,
            TotalFailures = 25
        };

        // Act
        var successRate = stats.SuccessRate;

        // Assert
        successRate.Should().Be(0.75);
    }

    [Theory]
    [InlineData(100, 10, 0.9)]
    [InlineData(100, 50, 0.5)]
    [InlineData(1000, 1, 0.999)]
    [InlineData(10, 3, 0.7)]
    public void SuccessRate_ShouldCalculateCorrectly_ForVariousScenarios(long totalOps, long failures, double expected)
    {
        // Arrange
        var stats = new CircuitBreakerStats
        {
            TotalOperations = totalOps,
            TotalFailures = failures
        };

        // Act
        var successRate = stats.SuccessRate;

        // Assert
        successRate.Should().BeApproximately(expected, 0.001);
    }

    [Fact]
    public void CircuitBreakerStats_ShouldSetPropertiesCorrectly()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var resetTime = now.AddHours(-1);

        // Act
        var stats = new CircuitBreakerStats
        {
            State = CircuitState.Open,
            FailureCount = 5,
            LastFailureTime = now,
            TotalOperations = 1000,
            TotalFailures = 50,
            StatsResetTime = resetTime
        };

        // Assert
        stats.State.Should().Be(CircuitState.Open);
        stats.FailureCount.Should().Be(5);
        stats.LastFailureTime.Should().Be(now);
        stats.TotalOperations.Should().Be(1000);
        stats.TotalFailures.Should().Be(50);
        stats.StatsResetTime.Should().Be(resetTime);
        stats.SuccessRate.Should().Be(0.95);
    }

    [Fact]
    public void CircuitBreakerStats_ShouldBeImmutableRecord()
    {
        // Arrange
        var stats1 = new CircuitBreakerStats
        {
            State = CircuitState.Closed,
            FailureCount = 0
        };

        // Act - create new instance with different state using with expression
        var stats2 = stats1 with { State = CircuitState.Open };

        // Assert - original should be unchanged
        stats1.State.Should().Be(CircuitState.Closed);
        stats2.State.Should().Be(CircuitState.Open);
    }

    [Fact]
    public void CircuitBreakerStats_HalfOpenState_ShouldBeSettable()
    {
        // Arrange & Act
        var stats = new CircuitBreakerStats
        {
            State = CircuitState.HalfOpen,
            FailureCount = 3
        };

        // Assert
        stats.State.Should().Be(CircuitState.HalfOpen);
    }
}
