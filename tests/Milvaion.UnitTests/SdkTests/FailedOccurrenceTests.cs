using FluentAssertions;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Domain.Enums;

namespace Milvaion.UnitTests.SdkTests;

[Trait("SDK Unit Tests", "FailedOccurrence entity unit tests.")]
public class FailedOccurrenceTests
{
    [Fact]
    public void FailedOccurrence_ShouldInitializeWithDefaultValues()
    {
        // Act
        var failedOccurrence = new FailedOccurrence();

        // Assert
        failedOccurrence.Id.Should().Be(Guid.Empty);
        failedOccurrence.JobId.Should().Be(Guid.Empty);
        failedOccurrence.OccurrenceId.Should().Be(Guid.Empty);
        failedOccurrence.CorrelationId.Should().Be(Guid.Empty);
        failedOccurrence.JobDisplayName.Should().BeNull();
        failedOccurrence.JobNameInWorker.Should().BeNull();
        failedOccurrence.WorkerId.Should().BeNull();
        failedOccurrence.JobData.Should().BeNull();
        failedOccurrence.Exception.Should().BeNull();
        failedOccurrence.RetryCount.Should().Be(0);
        failedOccurrence.FailureType.Should().Be(FailureType.Unknown);
        failedOccurrence.Resolved.Should().BeFalse();
        failedOccurrence.ResolvedAt.Should().BeNull();
        failedOccurrence.ResolvedBy.Should().BeNull();
        failedOccurrence.ResolutionNote.Should().BeNull();
        failedOccurrence.ResolutionAction.Should().BeNull();
        failedOccurrence.OriginalExecuteAt.Should().BeNull();
    }

    [Fact]
    public void FailedOccurrence_ShouldSetPropertiesCorrectly()
    {
        // Arrange
        var id = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var occurrenceId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // Act
        var failedOccurrence = new FailedOccurrence
        {
            Id = id,
            JobId = jobId,
            OccurrenceId = occurrenceId,
            CorrelationId = correlationId,
            JobDisplayName = "Send Email Job",
            JobNameInWorker = "SendEmailJob",
            WorkerId = "worker-01",
            JobData = "{\"to\": \"test@example.com\"}",
            Exception = "Connection timeout",
            FailedAt = now,
            RetryCount = 3,
            FailureType = FailureType.MaxRetriesExceeded,
            Resolved = false,
            OriginalExecuteAt = now.AddHours(-1)
        };

        // Assert
        failedOccurrence.Id.Should().Be(id);
        failedOccurrence.JobId.Should().Be(jobId);
        failedOccurrence.OccurrenceId.Should().Be(occurrenceId);
        failedOccurrence.CorrelationId.Should().Be(correlationId);
        failedOccurrence.JobDisplayName.Should().Be("Send Email Job");
        failedOccurrence.JobNameInWorker.Should().Be("SendEmailJob");
        failedOccurrence.WorkerId.Should().Be("worker-01");
        failedOccurrence.JobData.Should().Be("{\"to\": \"test@example.com\"}");
        failedOccurrence.Exception.Should().Be("Connection timeout");
        failedOccurrence.FailedAt.Should().Be(now);
        failedOccurrence.RetryCount.Should().Be(3);
        failedOccurrence.FailureType.Should().Be(FailureType.MaxRetriesExceeded);
    }

    [Fact]
    public void FailedOccurrence_Resolution_ShouldSetPropertiesCorrectly()
    {
        // Arrange
        var now = DateTime.UtcNow;

        // Act
        var failedOccurrence = new FailedOccurrence
        {
            Resolved = true,
            ResolvedAt = now,
            ResolvedBy = "admin",
            ResolutionNote = "Fixed data and re-queued",
            ResolutionAction = "Retried manually"
        };

        // Assert
        failedOccurrence.Resolved.Should().BeTrue();
        failedOccurrence.ResolvedAt.Should().Be(now);
        failedOccurrence.ResolvedBy.Should().Be("admin");
        failedOccurrence.ResolutionNote.Should().Be("Fixed data and re-queued");
        failedOccurrence.ResolutionAction.Should().Be("Retried manually");
    }

    [Fact]
    public void FailedOccurrence_Projections_TagList_ShouldExist()
        // Assert
        => FailedOccurrence.Projections.TagList.Should().NotBeNull();
}
