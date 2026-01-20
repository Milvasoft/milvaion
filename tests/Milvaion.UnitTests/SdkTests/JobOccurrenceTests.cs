using FluentAssertions;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Domain.JsonModels;

namespace Milvaion.UnitTests.SdkTests;

[Trait("SDK Unit Tests", "JobOccurrence entity unit tests.")]
public class JobOccurrenceTests
{
    [Fact]
    public void JobOccurrence_ShouldInitializeWithDefaultValues()
    {
        // Act
        var occurrence = new JobOccurrence();

        // Assert
        occurrence.Id.Should().Be(Guid.Empty);
        occurrence.JobName.Should().BeNull();
        occurrence.JobId.Should().Be(Guid.Empty);
        occurrence.JobVersion.Should().Be(0);
        occurrence.ZombieTimeoutMinutes.Should().BeNull();
        occurrence.CorrelationId.Should().Be(Guid.Empty);
        occurrence.WorkerId.Should().BeNull();
        occurrence.Status.Should().Be(JobOccurrenceStatus.Queued);
        occurrence.StartTime.Should().BeNull();
        occurrence.EndTime.Should().BeNull();
        occurrence.DurationMs.Should().BeNull();
        occurrence.Result.Should().BeNull();
        occurrence.Exception.Should().BeNull();
        occurrence.Logs.Should().BeEmpty();
        occurrence.DispatchRetryCount.Should().Be(0);
        occurrence.NextDispatchRetryAt.Should().BeNull();
        occurrence.LastHeartbeat.Should().BeNull();
        occurrence.StatusChangeLogs.Should().BeEmpty();
    }

    [Fact]
    public void JobOccurrence_ShouldSetPropertiesCorrectly()
    {
        // Arrange
        var id = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // Act
        var occurrence = new JobOccurrence
        {
            Id = id,
            JobName = "SendEmailJob",
            JobId = jobId,
            JobVersion = 2,
            ZombieTimeoutMinutes = 30,
            CorrelationId = correlationId,
            WorkerId = "worker-01",
            Status = JobOccurrenceStatus.Completed,
            StartTime = now.AddMinutes(-5),
            EndTime = now,
            DurationMs = 300000,
            Result = "Email sent successfully",
            Exception = null,
            CreatedAt = now.AddMinutes(-6),
            DispatchRetryCount = 1,
            NextDispatchRetryAt = now.AddMinutes(10),
            LastHeartbeat = now.AddSeconds(-30)
        };

        // Assert
        occurrence.Id.Should().Be(id);
        occurrence.JobName.Should().Be("SendEmailJob");
        occurrence.JobId.Should().Be(jobId);
        occurrence.JobVersion.Should().Be(2);
        occurrence.ZombieTimeoutMinutes.Should().Be(30);
        occurrence.CorrelationId.Should().Be(correlationId);
        occurrence.WorkerId.Should().Be("worker-01");
        occurrence.Status.Should().Be(JobOccurrenceStatus.Completed);
        occurrence.DurationMs.Should().Be(300000);
        occurrence.Result.Should().Be("Email sent successfully");
    }

    [Fact]
    public void JobOccurrence_Logs_ShouldStoreMultipleLogs()
    {
        // Arrange
        var occurrence = new JobOccurrence();
        var log1 = new OccurrenceLog { Timestamp = DateTime.UtcNow, Level = "Information", Message = "Log 1" };
        var log2 = new OccurrenceLog { Timestamp = DateTime.UtcNow, Level = "Warning", Message = "Log 2" };

        // Act
        occurrence.Logs.Add(log1);
        occurrence.Logs.Add(log2);

        // Assert
        occurrence.Logs.Should().HaveCount(2);
        occurrence.Logs[0].Message.Should().Be("Log 1");
        occurrence.Logs[1].Message.Should().Be("Log 2");
    }

    [Fact]
    public void JobOccurrence_StatusChangeLogs_ShouldTrackStatusHistory()
    {
        // Arrange
        var occurrence = new JobOccurrence();
        var changeLog1 = new OccurrenceStatusChangeLog
        {
            Timestamp = DateTime.UtcNow.AddMinutes(-5),
            From = JobOccurrenceStatus.Queued,
            To = JobOccurrenceStatus.Running
        };
        var changeLog2 = new OccurrenceStatusChangeLog
        {
            Timestamp = DateTime.UtcNow,
            From = JobOccurrenceStatus.Running,
            To = JobOccurrenceStatus.Completed
        };

        // Act
        occurrence.StatusChangeLogs.Add(changeLog1);
        occurrence.StatusChangeLogs.Add(changeLog2);

        // Assert
        occurrence.StatusChangeLogs.Should().HaveCount(2);
        occurrence.StatusChangeLogs[0].From.Should().Be(JobOccurrenceStatus.Queued);
        occurrence.StatusChangeLogs[0].To.Should().Be(JobOccurrenceStatus.Running);
        occurrence.StatusChangeLogs[1].From.Should().Be(JobOccurrenceStatus.Running);
        occurrence.StatusChangeLogs[1].To.Should().Be(JobOccurrenceStatus.Completed);
    }

    [Fact]
    public void Projections_AddFailedOccurrence_ShouldExist()
        // Assert
        => JobOccurrence.Projections.AddFailedOccurrence.Should().NotBeNull();

    [Fact]
    public void Projections_RetryFailed_ShouldExist()
        // Assert
        => JobOccurrence.Projections.RetryFailed.Should().NotBeNull();

    [Fact]
    public void Projections_UpdateLogs_ShouldExist()
        // Assert
        => JobOccurrence.Projections.UpdateLogs.Should().NotBeNull();

    [Fact]
    public void Projections_UpdateStatus_ShouldExist()
        // Assert
        => JobOccurrence.Projections.UpdateStatus.Should().NotBeNull();

    [Fact]
    public void Projections_RecoverLostJob_ShouldExist()
        // Assert
        => JobOccurrence.Projections.RecoverLostJob.Should().NotBeNull();

    [Fact]
    public void Projections_DetectZombie_ShouldExist()
        // Assert
        => JobOccurrence.Projections.DetectZombie.Should().NotBeNull();
}
