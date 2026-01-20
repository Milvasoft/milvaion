using FluentAssertions;
using Milvaion.Infrastructure.Services.Redis.Utils;

namespace Milvaion.UnitTests.InfrastructureTests;

[Trait("Infrastructure Unit Tests", "RedisCircuitBreakerOpenException unit tests.")]
public class RedisCircuitBreakerOpenExceptionTests
{
    [Fact]
    public void Constructor_WithMessage_ShouldSetMessage()
    {
        // Arrange
        var message = "Circuit breaker is open";

        // Act
        var exception = new RedisCircuitBreakerOpenException(message);

        // Assert
        exception.Message.Should().Be(message);
        exception.InnerException.Should().BeNull();
    }

    [Fact]
    public void Constructor_WithMessageAndInnerException_ShouldSetBoth()
    {
        // Arrange
        var message = "Circuit breaker is open";
        var innerException = new InvalidOperationException("Redis connection failed");

        // Act
        var exception = new RedisCircuitBreakerOpenException(message, innerException);

        // Assert
        exception.Message.Should().Be(message);
        exception.InnerException.Should().Be(innerException);
        exception.InnerException.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public void Exception_ShouldBeOfTypeException()
    {
        // Arrange & Act
        var exception = new RedisCircuitBreakerOpenException("test");

        // Assert
        exception.Should().BeAssignableTo<Exception>();
    }

    [Fact]
    public void Exception_ShouldBeThrowable()
    {
        // Arrange
        var message = "Circuit breaker is OPEN";

        // Act & Assert
        Action action = () => throw new RedisCircuitBreakerOpenException(message);

        action.Should().Throw<RedisCircuitBreakerOpenException>()
              .WithMessage(message);
    }

    [Fact]
    public void Exception_WithInnerException_ShouldPreserveStackTrace()
    {
        // Arrange
        var innerException = new TimeoutException("Redis timeout");
        var exception = new RedisCircuitBreakerOpenException("Circuit open", innerException);

        // Act & Assert
        exception.InnerException.Should().BeOfType<TimeoutException>();
        exception.InnerException.Message.Should().Be("Redis timeout");
    }

    [Fact]
    public void Exception_ShouldSupportCatching()
    {
        // Arrange
        var message = "Test exception";
        bool caught;

        // Act
        try
        {
            throw new RedisCircuitBreakerOpenException(message);
        }
        catch (RedisCircuitBreakerOpenException ex)
        {
            caught = true;
            ex.Message.Should().Be(message);
        }

        // Assert
        caught.Should().BeTrue();
    }

    [Fact]
    public void Exception_ShouldBeSerializableMessage()
    {
        // Arrange
        var specialMessage = "Circuit breaker open: connection timeout after 5000ms";

        // Act
        var exception = new RedisCircuitBreakerOpenException(specialMessage);

        // Assert
        exception.Message.Should().Contain("Circuit breaker open");
        exception.Message.Should().Contain("5000ms");
    }

    [Fact]
    public void Exception_ChainedInnerExceptions_ShouldWork()
    {
        // Arrange
        var level1 = new ArgumentException("Invalid argument");
        var level2 = new InvalidOperationException("Operation failed", level1);
        var exception = new RedisCircuitBreakerOpenException("Circuit open", level2);

        // Assert
        exception.InnerException.Should().BeOfType<InvalidOperationException>();
        exception.InnerException.InnerException.Should().BeOfType<ArgumentException>();
    }
}
