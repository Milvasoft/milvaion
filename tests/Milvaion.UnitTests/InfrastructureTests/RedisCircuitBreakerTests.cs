using FluentAssertions;
using Microsoft.Extensions.Logging;
using Milvaion.Infrastructure.Services.Redis;
using Milvaion.Infrastructure.Services.Redis.Utils;
using Moq;
using StackExchange.Redis;

namespace Milvaion.UnitTests.InfrastructureTests;

[Trait("Infrastructure Unit Tests", "RedisCircuitBreaker unit tests.")]
public class RedisCircuitBreakerTests
{
    private readonly Mock<ILoggerFactory> _loggerFactoryMock;
    private readonly RedisCircuitBreaker _circuitBreaker;

    public RedisCircuitBreakerTests()
    {
        _loggerFactoryMock = new Mock<ILoggerFactory>();
        _loggerFactoryMock.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

        _circuitBreaker = new RedisCircuitBreaker(_loggerFactoryMock.Object);
    }

    [Fact]
    public void State_ShouldBeClosedInitially()
        // Assert
        => _circuitBreaker.State.Should().Be(CircuitState.Closed);

    [Fact]
    public async Task ExecuteAsync_ShouldReturnResult_WhenOperationSucceeds()
    {
        // Arrange
        var expectedResult = "success";

        // Act
        var result = await _circuitBreaker.ExecuteAsync(
            () => Task.FromResult(expectedResult),
            operationName: "TestOperation");

        // Assert
        result.Should().Be(expectedResult);
        _circuitBreaker.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldIncrementTotalOperations_WhenOperationSucceeds()
    {
        // Act
        await _circuitBreaker.ExecuteAsync(() => Task.FromResult("result"), operationName: "TestOp");
        await _circuitBreaker.ExecuteAsync(() => Task.FromResult("result"), operationName: "TestOp");
        await _circuitBreaker.ExecuteAsync(() => Task.FromResult("result"), operationName: "TestOp");

        // Assert
        var stats = _circuitBreaker.GetStats();
        stats.TotalOperations.Should().Be(3);
        stats.TotalFailures.Should().Be(0);
        stats.SuccessRate.Should().Be(1.0);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldUseFallback_WhenOperationFails()
    {
        // Arrange
        var fallbackResult = "fallback";

        // Act
        var result = await _circuitBreaker.ExecuteAsync(
            () => throw new RedisException("Connection failed"),
            fallback: () => Task.FromResult(fallbackResult),
            operationName: "TestOperation");

        // Assert
        result.Should().Be(fallbackResult);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldIncrementFailureCount_WhenOperationFails()
    {
        // Act
        await _circuitBreaker.ExecuteAsync(
            () => throw new RedisException("Fail"),
            fallback: () => Task.FromResult("fallback"),
            operationName: "TestOp");

        // Assert
        var stats = _circuitBreaker.GetStats();
        stats.FailureCount.Should().Be(1);
        stats.TotalFailures.Should().Be(1);
        stats.LastFailureTime.Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldOpenCircuit_AfterThresholdFailures()
    {
        // Arrange - Fail 5 times (default threshold)
        for (int i = 0; i < 5; i++)
        {
            await _circuitBreaker.ExecuteAsync(
                () => throw new RedisException("Fail"),
                fallback: () => Task.FromResult("fallback"),
                operationName: "TestOp");
        }

        // Assert
        _circuitBreaker.State.Should().Be(CircuitState.Open);
        var stats = _circuitBreaker.GetStats();
        stats.FailureCount.Should().Be(5);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldThrowException_WhenCircuitOpenAndNoFallback()
    {
        // Arrange - Open the circuit
        for (int i = 0; i < 5; i++)
        {
            await _circuitBreaker.ExecuteAsync(
                () => throw new RedisException("Fail"),
                fallback: () => Task.FromResult("fallback"),
                operationName: "TestOp");
        }

        // Act & Assert
        var action = () => _circuitBreaker.ExecuteAsync<string>(
            () => throw new RedisException("Fail"),
            fallback: null,
            operationName: "TestOp");

        await action.Should().ThrowAsync<RedisCircuitBreakerOpenException>();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldResetFailureCount_WhenOperationSucceeds()
    {
        // Arrange - Cause some failures first
        await _circuitBreaker.ExecuteAsync(
            () => throw new RedisException("Fail"),
            fallback: () => Task.FromResult("fallback"),
            operationName: "TestOp");

        await _circuitBreaker.ExecuteAsync(
            () => throw new RedisException("Fail"),
            fallback: () => Task.FromResult("fallback"),
            operationName: "TestOp");

        var statsAfterFailures = _circuitBreaker.GetStats();
        statsAfterFailures.FailureCount.Should().Be(2);

        // Act - Successful operation
        await _circuitBreaker.ExecuteAsync(
            () => Task.FromResult("success"),
            operationName: "TestOp");

        // Assert
        var stats = _circuitBreaker.GetStats();
        stats.FailureCount.Should().Be(0);
        stats.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldHandleTimeoutException()
    {
        // Act
        var result = await _circuitBreaker.ExecuteAsync(
            () => throw new TimeoutException("Operation timed out"),
            fallback: () => Task.FromResult("timeout-fallback"),
            operationName: "TestOp");

        // Assert
        result.Should().Be("timeout-fallback");
        var stats = _circuitBreaker.GetStats();
        stats.FailureCount.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldHandleRedisConnectionException()
    {
        // Act
        var result = await _circuitBreaker.ExecuteAsync(
            () => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Cannot connect"),
            fallback: () => Task.FromResult("connection-fallback"),
            operationName: "TestOp");

        // Assert
        result.Should().Be("connection-fallback");
    }

    [Fact]
    public void GetStats_ShouldReturnValidStats()
    {
        // Act
        var stats = _circuitBreaker.GetStats();

        // Assert
        stats.Should().NotBeNull();
        stats.State.Should().Be(CircuitState.Closed);
        stats.FailureCount.Should().Be(0);
        stats.TotalOperations.Should().Be(0);
        stats.TotalFailures.Should().Be(0);
        stats.StatsResetTime.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldUseFallbackWhenCircuitIsOpen()
    {
        // Arrange - Open the circuit
        for (int i = 0; i < 5; i++)
        {
            await _circuitBreaker.ExecuteAsync(
                () => throw new RedisException("Fail"),
                fallback: () => Task.FromResult("initial-fallback"),
                operationName: "TestOp");
        }

        _circuitBreaker.State.Should().Be(CircuitState.Open);

        // Act - Try operation when circuit is open
        var result = await _circuitBreaker.ExecuteAsync(
            () => Task.FromResult("should-not-execute"),
            fallback: () => Task.FromResult("circuit-open-fallback"),
            operationName: "TestOp");

        // Assert
        result.Should().Be("circuit-open-fallback");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRespectCancellation()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act & Assert - Regular exception should propagate, not be handled as Redis failure
#pragma warning disable AsyncFixer06 // Task<T> to Task conversion silently discards result
        Func<Task> action = () => _circuitBreaker.ExecuteAsync<string>(
            () => throw new OperationCanceledException(),
            operationName: "TestOp",
            cancellationToken: cts.Token);
#pragma warning restore AsyncFixer06 // Task<T> to Task conversion silently discards result

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ExecuteAsync_MultipleSuccessfulOperations_ShouldMaintainClosedState()
    {
        // Act
        for (int i = 0; i < 10; i++)
        {
            await _circuitBreaker.ExecuteAsync(
                () => Task.FromResult($"result-{i}"),
                operationName: "TestOp");
        }

        // Assert
        _circuitBreaker.State.Should().Be(CircuitState.Closed);
        var stats = _circuitBreaker.GetStats();
        stats.TotalOperations.Should().Be(10);
        stats.TotalFailures.Should().Be(0);
        stats.SuccessRate.Should().Be(1.0);
    }
}
