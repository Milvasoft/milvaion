using FluentAssertions;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Domain.Enums;

namespace Milvaion.UnitTests.SdkTests;

[Trait("SDK Unit Tests", "ScheduledJob entity unit tests.")]
public class ScheduledJobTests
{
    [Fact]
    public void ScheduledJob_ShouldInitializeWithDefaultValues()
    {
        // Act
        var job = new ScheduledJob();

        // Assert
        job.Id.Should().Be(Guid.Empty);
        job.DisplayName.Should().BeNull();
        job.Description.Should().BeNull();
        job.Tags.Should().BeNull();
        job.JobData.Should().BeNull();
        job.CronExpression.Should().BeNull();
        job.IsActive.Should().BeTrue();
        job.ConcurrentExecutionPolicy.Should().Be(ConcurrentExecutionPolicy.Skip);
        job.WorkerId.Should().BeNull();
        job.JobNameInWorker.Should().BeNull();
        job.RoutingPattern.Should().BeNull();
        job.ZombieTimeoutMinutes.Should().BeNull();
        job.Version.Should().Be(1);
        job.JobVersions.Should().BeEmpty();
        job.AutoDisableSettings.Should().NotBeNull();
    }

    [Fact]
    public void ScheduledJob_ShouldSetPropertiesCorrectly()
    {
        // Arrange
        var id = Guid.NewGuid();
        var executeAt = DateTime.UtcNow.AddHours(1);

        // Act
        var job = new ScheduledJob
        {
            Id = id,
            DisplayName = "Test Job",
            Description = "A test job description",
            Tags = "email,notification",
            JobData = "{\"to\": \"test@example.com\"}",
            ExecuteAt = executeAt,
            CronExpression = "0 9 * * MON",
            IsActive = true,
            ConcurrentExecutionPolicy = ConcurrentExecutionPolicy.Queue,
            WorkerId = "worker-01",
            JobNameInWorker = "SendEmailJob",
            RoutingPattern = "jobs.email.*",
            ZombieTimeoutMinutes = 30,
            Version = 2,
            JobVersions = ["v1", "v2"]
        };

        // Assert
        job.Id.Should().Be(id);
        job.DisplayName.Should().Be("Test Job");
        job.Description.Should().Be("A test job description");
        job.Tags.Should().Be("email,notification");
        job.JobData.Should().Be("{\"to\": \"test@example.com\"}");
        job.ExecuteAt.Should().Be(executeAt);
        job.CronExpression.Should().Be("0 9 * * MON");
        job.IsActive.Should().BeTrue();
        job.ConcurrentExecutionPolicy.Should().Be(ConcurrentExecutionPolicy.Queue);
        job.WorkerId.Should().Be("worker-01");
        job.JobNameInWorker.Should().Be("SendEmailJob");
        job.RoutingPattern.Should().Be("jobs.email.*");
        job.ZombieTimeoutMinutes.Should().Be(30);
        job.Version.Should().Be(2);
        job.JobVersions.Should().HaveCount(2);
    }

    [Fact]
    public void FixJobData_ShouldReturnNull_WhenJobDataIsNull()
    {
        // Act
        var result = ScheduledJob.FixJobData(null);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void FixJobData_ShouldReturnEmptyJson_WhenJobDataIsEmpty()
    {
        // Act
        var result = ScheduledJob.FixJobData("");

        // Assert
        result.Should().Be("{}");
    }

    [Fact]
    public void FixJobData_ShouldReturnOriginal_WhenJobDataIsValidJson()
    {
        // Arrange
        var validJson = "{\"key\": \"value\", \"count\": 42}";

        // Act
        var result = ScheduledJob.FixJobData(validJson);

        // Assert
        result.Should().Be(validJson);
    }

    [Fact]
    public void FixJobData_ShouldReturnEmptyJson_WhenJobDataIsInvalidJson()
    {
        // Arrange
        var invalidJson = "{invalid json";

        // Act
        var result = ScheduledJob.FixJobData(invalidJson);

        // Assert
        result.Should().Be("{}");
    }

    [Fact]
    public void FixJobData_Instance_ShouldUpdateJobDataProperty()
    {
        // Arrange
        var job = new ScheduledJob { JobData = "{invalid" };

        // Act
        var result = job.FixJobData();

        // Assert
        result.Should().Be("{}");
        job.JobData.Should().Be("{}");
    }

    [Fact]
    public void FixJobData_ShouldPreserveComplexValidJson()
    {
        // Arrange
        var complexJson = "{\"array\": [1, 2, 3], \"nested\": {\"key\": \"value\"}, \"bool\": true}";

        // Act
        var result = ScheduledJob.FixJobData(complexJson);

        // Assert
        result.Should().Be(complexJson);
    }

    [Fact]
    public void Projections_TagList_ShouldExist()
        // Assert
        => ScheduledJob.Projections.TagList.Should().NotBeNull();

    [Fact]
    public void Projections_OnlyId_ShouldExist()
        // Assert
        => ScheduledJob.Projections.OnlyId.Should().NotBeNull();

    [Fact]
    public void Projections_CacheJob_ShouldExist()
        // Assert
        => ScheduledJob.Projections.CacheJob.Should().NotBeNull();

    [Fact]
    public void Projections_RetryFailedOccurrence_ShouldExist()
        // Assert
        => ScheduledJob.Projections.RetryFailedOccurrence.Should().NotBeNull();

    [Fact]
    public void Projections_CircuitBreaker_ShouldExist()
        // Assert
        => ScheduledJob.Projections.CircuitBreaker.Should().NotBeNull();
}
