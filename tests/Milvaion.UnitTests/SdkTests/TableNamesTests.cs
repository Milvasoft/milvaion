using FluentAssertions;
using Milvasoft.Milvaion.Sdk.Domain;

namespace Milvaion.UnitTests.SdkTests;

[Trait("SDK Unit Tests", "SchedulerTableNames unit tests.")]
public class SchedulerTableNamesTests
{
    [Fact]
    public void ScheduledJobs_ShouldHaveCorrectValue()
        // Assert
        => SchedulerTableNames.ScheduledJobs.Should().Be("ScheduledJobs");

    [Fact]
    public void JobOccurrences_ShouldHaveCorrectValue()
        // Assert
        => SchedulerTableNames.JobOccurrences.Should().Be("JobOccurrences");

    [Fact]
    public void FailedOccurrences_ShouldHaveCorrectValue()
        // Assert
        => SchedulerTableNames.FailedOccurrences.Should().Be("FailedOccurrences");
}
