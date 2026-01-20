using FluentAssertions;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Domain.JsonModels;

namespace Milvaion.UnitTests.SdkTests;

[Trait("SDK Unit Tests", "JobAutoDisableSettings unit tests.")]
public class JobAutoDisableSettingsTests
{
    [Fact]
    public void JobAutoDisableSettings_ShouldInitializeWithDefaultValues()
    {
        // Act
        var settings = new JobAutoDisableSettings();

        // Assert
        settings.ConsecutiveFailureCount.Should().Be(0);
        settings.LastFailureTime.Should().BeNull();
        settings.DisabledAt.Should().BeNull();
        settings.DisableReason.Should().BeNull();
        settings.Enabled.Should().BeNull();
        settings.Threshold.Should().BeNull();
    }

    [Fact]
    public void JobAutoDisableSettings_ShouldSetPropertiesCorrectly()
    {
        // Arrange
        var now = DateTime.UtcNow;

        // Act
        var settings = new JobAutoDisableSettings
        {
            ConsecutiveFailureCount = 5,
            LastFailureTime = now.AddMinutes(-10),
            DisabledAt = now,
            DisableReason = "Auto-disabled after 5 consecutive failures",
            Enabled = true,
            Threshold = 3
        };

        // Assert
        settings.ConsecutiveFailureCount.Should().Be(5);
        settings.LastFailureTime.Should().Be(now.AddMinutes(-10));
        settings.DisabledAt.Should().Be(now);
        settings.DisableReason.Should().Be("Auto-disabled after 5 consecutive failures");
        settings.Enabled.Should().BeTrue();
        settings.Threshold.Should().Be(3);
    }

    [Fact]
    public void JobAutoDisableSettings_IncrementFailureCount_ShouldWork()
    {
        // Arrange
        var settings = new JobAutoDisableSettings();

        // Act
        settings.ConsecutiveFailureCount++;
        settings.ConsecutiveFailureCount++;
        settings.ConsecutiveFailureCount++;

        // Assert
        settings.ConsecutiveFailureCount.Should().Be(3);
    }

    [Fact]
    public void JobAutoDisableSettings_ResetOnSuccess_ShouldWork()
    {
        // Arrange
        var settings = new JobAutoDisableSettings
        {
            ConsecutiveFailureCount = 5,
            LastFailureTime = DateTime.UtcNow.AddMinutes(-10)
        };

        // Act - Reset on success
        settings.ConsecutiveFailureCount = 0;
        settings.LastFailureTime = null;

        // Assert
        settings.ConsecutiveFailureCount.Should().Be(0);
        settings.LastFailureTime.Should().BeNull();
    }
}

[Trait("SDK Unit Tests", "OccurrenceLog unit tests.")]
public class OccurrenceLogTests
{
    [Fact]
    public void OccurrenceLog_ShouldInitializeWithDefaultValues()
    {
        // Act
        var log = new OccurrenceLog();

        // Assert
        log.Timestamp.Should().Be(default);
        log.Level.Should().BeNull();
        log.Message.Should().BeNull();
        log.Data.Should().BeNull();
        log.Category.Should().BeNull();
        log.ExceptionType.Should().BeNull();
    }

    [Fact]
    public void OccurrenceLog_ShouldSetPropertiesCorrectly()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var data = new Dictionary<string, object>
        {
            ["userId"] = 123,
            ["action"] = "ProcessOrder"
        };

        // Act
        var log = new OccurrenceLog
        {
            Timestamp = now,
            Level = "Information",
            Message = "Order processed successfully",
            Data = data,
            Category = "OrderProcessing",
            ExceptionType = null
        };

        // Assert
        log.Timestamp.Should().Be(now);
        log.Level.Should().Be("Information");
        log.Message.Should().Be("Order processed successfully");
        log.Data.Should().NotBeNull();
        log.Data.Should().ContainKey("userId");
        log.Category.Should().Be("OrderProcessing");
    }

    [Fact]
    public void OccurrenceLog_ErrorLog_ShouldIncludeExceptionType()
    {
        // Act
        var log = new OccurrenceLog
        {
            Timestamp = DateTime.UtcNow,
            Level = "Error",
            Message = "Connection failed",
            ExceptionType = "SqlException",
            Data = new Dictionary<string, object>
            {
                ["StackTrace"] = "at System.Data..."
            }
        };

        // Assert
        log.Level.Should().Be("Error");
        log.ExceptionType.Should().Be("SqlException");
        log.Data.Should().ContainKey("StackTrace");
    }
}

[Trait("SDK Unit Tests", "OccurrenceStatusChangeLog unit tests.")]
public class OccurrenceStatusChangeLogTests
{
    [Fact]
    public void OccurrenceStatusChangeLog_ShouldInitializeWithDefaultValues()
    {
        // Act
        var changeLog = new OccurrenceStatusChangeLog();

        // Assert
        changeLog.Timestamp.Should().Be(default);
        changeLog.From.Should().Be(JobOccurrenceStatus.Queued);
        changeLog.To.Should().Be(JobOccurrenceStatus.Queued);
    }

    [Fact]
    public void OccurrenceStatusChangeLog_ShouldSetPropertiesCorrectly()
    {
        // Arrange
        var now = DateTime.UtcNow;

        // Act
        var changeLog = new OccurrenceStatusChangeLog
        {
            Timestamp = now,
            From = JobOccurrenceStatus.Queued,
            To = JobOccurrenceStatus.Running
        };

        // Assert
        changeLog.Timestamp.Should().Be(now);
        changeLog.From.Should().Be(JobOccurrenceStatus.Queued);
        changeLog.To.Should().Be(JobOccurrenceStatus.Running);
    }

    [Fact]
    public void OccurrenceStatusChangeLog_ShouldTrackAllStatusTransitions()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var logs = new List<OccurrenceStatusChangeLog>
        {
            new() { Timestamp = now.AddMinutes(-10), From = JobOccurrenceStatus.Queued, To = JobOccurrenceStatus.Running },
            new() { Timestamp = now.AddMinutes(-5), From = JobOccurrenceStatus.Running, To = JobOccurrenceStatus.Failed },
            new() { Timestamp = now, From = JobOccurrenceStatus.Failed, To = JobOccurrenceStatus.Running }
        };

        // Assert
        logs.Should().HaveCount(3);
        logs[0].To.Should().Be(JobOccurrenceStatus.Running);
        logs[1].To.Should().Be(JobOccurrenceStatus.Failed);
        logs[2].To.Should().Be(JobOccurrenceStatus.Running);
    }
}
