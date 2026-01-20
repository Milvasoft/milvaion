using FluentAssertions;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Utils;

namespace Milvaion.UnitTests.SdkTests;

[Trait("SDK Unit Tests", "MilvaionSdkExtensions unit tests.")]
public class MilvaionSdkExtensionsTests
{
    [Theory]
    [InlineData(JobOccurrenceStatus.Completed, true)]
    [InlineData(JobOccurrenceStatus.Failed, true)]
    [InlineData(JobOccurrenceStatus.Cancelled, true)]
    [InlineData(JobOccurrenceStatus.TimedOut, true)]
    [InlineData(JobOccurrenceStatus.Unknown, true)]
    [InlineData(JobOccurrenceStatus.Queued, false)]
    [InlineData(JobOccurrenceStatus.Running, false)]
    public void IsFinalStatus_ShouldReturnExpectedResult(JobOccurrenceStatus status, bool expectedResult)
    {
        // Act
        var result = status.IsFinalStatus();

        // Assert
        result.Should().Be(expectedResult);
    }

    [Fact]
    public void IsFinalStatus_Completed_ShouldReturnTrue()
    {
        // Arrange
        var status = JobOccurrenceStatus.Completed;

        // Act
        var result = status.IsFinalStatus();

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsFinalStatus_Failed_ShouldReturnTrue()
    {
        // Arrange
        var status = JobOccurrenceStatus.Failed;

        // Act
        var result = status.IsFinalStatus();

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsFinalStatus_Cancelled_ShouldReturnTrue()
    {
        // Arrange
        var status = JobOccurrenceStatus.Cancelled;

        // Act
        var result = status.IsFinalStatus();

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsFinalStatus_TimedOut_ShouldReturnTrue()
    {
        // Arrange
        var status = JobOccurrenceStatus.TimedOut;

        // Act
        var result = status.IsFinalStatus();

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsFinalStatus_Unknown_ShouldReturnTrue()
    {
        // Arrange
        var status = JobOccurrenceStatus.Unknown;

        // Act
        var result = status.IsFinalStatus();

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void IsFinalStatus_Queued_ShouldReturnFalse()
    {
        // Arrange
        var status = JobOccurrenceStatus.Queued;

        // Act
        var result = status.IsFinalStatus();

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsFinalStatus_Running_ShouldReturnFalse()
    {
        // Arrange
        var status = JobOccurrenceStatus.Running;

        // Act
        var result = status.IsFinalStatus();

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void IsFinalStatus_AllFinalStatuses_ShouldReturnTrue()
    {
        // Arrange
        var finalStatuses = new[]
        {
            JobOccurrenceStatus.Completed,
            JobOccurrenceStatus.Failed,
            JobOccurrenceStatus.Cancelled,
            JobOccurrenceStatus.TimedOut,
            JobOccurrenceStatus.Unknown
        };

        // Act & Assert
        foreach (var status in finalStatuses)
        {
            status.IsFinalStatus().Should().BeTrue($"because {status} is a final status");
        }
    }

    [Fact]
    public void IsFinalStatus_AllNonFinalStatuses_ShouldReturnFalse()
    {
        // Arrange
        var nonFinalStatuses = new[]
        {
            JobOccurrenceStatus.Queued,
            JobOccurrenceStatus.Running
        };

        // Act & Assert
        foreach (var status in nonFinalStatuses)
        {
            status.IsFinalStatus().Should().BeFalse($"because {status} is not a final status");
        }
    }

    [Fact]
    public void IsFinalStatus_FinalStatusCount_ShouldBeFive()
    {
        // Arrange
        var allStatuses = Enum.GetValues<JobOccurrenceStatus>();

        // Act
        var finalStatusCount = allStatuses.Count(s => s.IsFinalStatus());

        // Assert
        finalStatusCount.Should().Be(5, "because there are 5 final statuses: Completed, Failed, Cancelled, TimedOut, Unknown");
    }

    [Fact]
    public void IsFinalStatus_NonFinalStatusCount_ShouldBeTwo()
    {
        // Arrange
        var allStatuses = Enum.GetValues<JobOccurrenceStatus>();

        // Act
        var nonFinalStatusCount = allStatuses.Count(s => !s.IsFinalStatus());

        // Assert
        nonFinalStatusCount.Should().Be(2, "because there are 2 non-final statuses: Queued, Running");
    }
}
