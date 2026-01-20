using FluentAssertions;
using Milvaion.Infrastructure.Services.Redis.Utils;

namespace Milvaion.UnitTests.InfrastructureTests;

[Trait("Infrastructure Unit Tests", "CircuitState enum unit tests.")]
public class CircuitStateTests
{
    [Fact]
    public void CircuitState_Closed_ShouldHaveValueZero()
        // Assert
        => ((int)CircuitState.Closed).Should().Be(0);

    [Fact]
    public void CircuitState_Open_ShouldHaveValueOne()
        // Assert
        => ((int)CircuitState.Open).Should().Be(1);

    [Fact]
    public void CircuitState_HalfOpen_ShouldHaveValueTwo()
        // Assert
        => ((int)CircuitState.HalfOpen).Should().Be(2);

    [Fact]
    public void CircuitState_ShouldHaveThreeValues()
    {
        // Act
        var values = Enum.GetValues<CircuitState>();

        // Assert
        values.Should().HaveCount(3);
    }

    [Fact]
    public void CircuitState_ShouldContainAllExpectedValues()
    {
        // Act
        var values = Enum.GetValues<CircuitState>();

        // Assert
        values.Should().Contain(CircuitState.Closed);
        values.Should().Contain(CircuitState.Open);
        values.Should().Contain(CircuitState.HalfOpen);
    }

    [Theory]
    [InlineData(CircuitState.Closed, "Closed")]
    [InlineData(CircuitState.Open, "Open")]
    [InlineData(CircuitState.HalfOpen, "HalfOpen")]
    public void CircuitState_ToString_ShouldReturnCorrectName(CircuitState state, string expectedName)
    {
        // Act
        var name = state.ToString();

        // Assert
        name.Should().Be(expectedName);
    }

    [Theory]
    [InlineData("Closed", CircuitState.Closed)]
    [InlineData("Open", CircuitState.Open)]
    [InlineData("HalfOpen", CircuitState.HalfOpen)]
    public void CircuitState_Parse_ShouldReturnCorrectValue(string name, CircuitState expected)
    {
        // Act
        var result = Enum.Parse<CircuitState>(name);

        // Assert
        result.Should().Be(expected);
    }
}
