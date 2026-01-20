using FluentAssertions;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Utils;

namespace Milvaion.UnitTests.SdkTests;

[Trait("SDK Unit Tests", "JobOccurrenceStatus enum unit tests.")]
public class JobOccurrenceStatusTests
{
    [Theory]
    [InlineData(JobOccurrenceStatus.Completed, true)]
    [InlineData(JobOccurrenceStatus.Failed, true)]
    [InlineData(JobOccurrenceStatus.Cancelled, true)]
    [InlineData(JobOccurrenceStatus.TimedOut, true)]
    [InlineData(JobOccurrenceStatus.Unknown, true)]
    [InlineData(JobOccurrenceStatus.Queued, false)]
    [InlineData(JobOccurrenceStatus.Running, false)]
    public void IsFinalStatus_ShouldReturnCorrectValue(JobOccurrenceStatus status, bool expectedResult)
    {
        // Act
        var result = status.IsFinalStatus();

        // Assert
        result.Should().Be(expectedResult);
    }

    [Fact]
    public void JobOccurrenceStatus_ShouldHaveCorrectValues()
    {
        // Assert
        ((int)JobOccurrenceStatus.Queued).Should().Be(0);
        ((int)JobOccurrenceStatus.Running).Should().Be(1);
        ((int)JobOccurrenceStatus.Completed).Should().Be(2);
        ((int)JobOccurrenceStatus.Failed).Should().Be(3);
        ((int)JobOccurrenceStatus.Cancelled).Should().Be(4);
        ((int)JobOccurrenceStatus.TimedOut).Should().Be(5);
        ((int)JobOccurrenceStatus.Unknown).Should().Be(6);
    }

    [Fact]
    public void JobOccurrenceStatus_ShouldHaveSevenValues()
    {
        // Act
        var values = Enum.GetValues<JobOccurrenceStatus>();

        // Assert
        values.Should().HaveCount(7);
    }
}

[Trait("SDK Unit Tests", "ConcurrentExecutionPolicy enum unit tests.")]
public class ConcurrentExecutionPolicyTests
{
    [Fact]
    public void ConcurrentExecutionPolicy_ShouldHaveCorrectValues()
    {
        // Assert
        ((int)ConcurrentExecutionPolicy.Skip).Should().Be(0);
        ((int)ConcurrentExecutionPolicy.Queue).Should().Be(1);
    }

    [Fact]
    public void ConcurrentExecutionPolicy_ShouldHaveTwoValues()
    {
        // Act
        var values = Enum.GetValues<ConcurrentExecutionPolicy>();

        // Assert
        values.Should().HaveCount(2);
    }
}

[Trait("SDK Unit Tests", "FailureType enum unit tests.")]
public class FailureTypeTests
{
    [Fact]
    public void FailureType_ShouldHaveCorrectValues()
    {
        // Assert
        ((int)FailureType.Unknown).Should().Be(0);
        ((int)FailureType.MaxRetriesExceeded).Should().Be(1);
        ((int)FailureType.Timeout).Should().Be(2);
        ((int)FailureType.WorkerCrash).Should().Be(3);
        ((int)FailureType.InvalidJobData).Should().Be(4);
        ((int)FailureType.ExternalDependencyFailure).Should().Be(5);
        ((int)FailureType.UnhandledException).Should().Be(6);
        ((int)FailureType.Cancelled).Should().Be(7);
        ((int)FailureType.ZombieDetection).Should().Be(8);
    }

    [Fact]
    public void FailureType_ShouldHaveNineValues()
    {
        // Act
        var values = Enum.GetValues<FailureType>();

        // Assert
        values.Should().HaveCount(9);
    }
}

[Trait("SDK Unit Tests", "WorkerStatus enum unit tests.")]
public class WorkerStatusTests
{
    [Fact]
    public void WorkerStatus_ShouldHaveCorrectValues()
    {
        // Assert
        ((int)WorkerStatus.Active).Should().Be(0);
        ((int)WorkerStatus.Inactive).Should().Be(1);
        ((int)WorkerStatus.Zombie).Should().Be(2);
        ((int)WorkerStatus.Shutdown).Should().Be(3);
    }

    [Fact]
    public void WorkerStatus_ShouldHaveFourValues()
    {
        // Act
        var values = Enum.GetValues<WorkerStatus>();

        // Assert
        values.Should().HaveCount(4);
    }
}
