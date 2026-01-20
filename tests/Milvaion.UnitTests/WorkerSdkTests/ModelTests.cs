using FluentAssertions;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Domain.JsonModels;
using Milvasoft.Milvaion.Sdk.Worker.Persistence;

namespace Milvaion.UnitTests.WorkerSdkTests;

[Trait("SDK Unit Tests", "SyncResult unit tests.")]
public class SyncResultTests
{
    [Fact]
    public void SyncResult_ShouldInitializeWithDefaultValues()
    {
        // Act
        var result = new SyncResult();

        // Assert
        result.Success.Should().BeFalse();
        result.Skipped.Should().BeFalse();
        result.SyncedCount.Should().Be(0);
        result.FailedCount.Should().Be(0);
        result.Message.Should().BeNull();
    }

    [Fact]
    public void SyncResult_ShouldSetPropertiesCorrectly()
    {
        // Act
        var result = new SyncResult
        {
            Success = true,
            Skipped = false,
            SyncedCount = 10,
            FailedCount = 2,
            Message = "Sync completed"
        };

        // Assert
        result.Success.Should().BeTrue();
        result.Skipped.Should().BeFalse();
        result.SyncedCount.Should().Be(10);
        result.FailedCount.Should().Be(2);
        result.Message.Should().Be("Sync completed");
    }
}

[Trait("SDK Unit Tests", "StoredStatusUpdate unit tests.")]
public class StoredStatusUpdateTests
{
    [Fact]
    public void StoredStatusUpdate_ShouldInitializeWithDefaultValues()
    {
        // Act
        var update = new StoredStatusUpdate();

        // Assert
        update.Id.Should().Be(0);
        update.CorrelationId.Should().Be(Guid.Empty);
        update.JobId.Should().Be(Guid.Empty);
        update.WorkerId.Should().BeNull();
        update.Status.Should().Be(JobOccurrenceStatus.Queued);
        update.StartTime.Should().BeNull();
        update.EndTime.Should().BeNull();
        update.DurationMs.Should().BeNull();
        update.Result.Should().BeNull();
        update.Exception.Should().BeNull();
        update.RetryCount.Should().Be(0);
    }

    [Fact]
    public void StoredStatusUpdate_ShouldSetPropertiesCorrectly()
    {
        // Arrange
        var correlationId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // Act
        var update = new StoredStatusUpdate
        {
            Id = 1,
            CorrelationId = correlationId,
            JobId = jobId,
            WorkerId = "test-worker",
            Status = JobOccurrenceStatus.Completed,
            StartTime = now.AddMinutes(-5),
            EndTime = now,
            DurationMs = 300000,
            Result = "Job completed successfully",
            Exception = null,
            CreatedAt = now.AddMinutes(-5),
            RetryCount = 0
        };

        // Assert
        update.Id.Should().Be(1);
        update.CorrelationId.Should().Be(correlationId);
        update.JobId.Should().Be(jobId);
        update.WorkerId.Should().Be("test-worker");
        update.Status.Should().Be(JobOccurrenceStatus.Completed);
        update.DurationMs.Should().Be(300000);
        update.Result.Should().Be("Job completed successfully");
    }
}

[Trait("SDK Unit Tests", "StoredLog unit tests.")]
public class StoredLogTests
{
    [Fact]
    public void StoredLog_ShouldInitializeWithDefaultValues()
    {
        // Act
        var storedLog = new StoredLog();

        // Assert
        storedLog.Id.Should().Be(0);
        storedLog.CorrelationId.Should().Be(Guid.Empty);
        storedLog.WorkerId.Should().BeNull();
        storedLog.Log.Should().BeNull();
        storedLog.RetryCount.Should().Be(0);
    }

    [Fact]
    public void StoredLog_ShouldSetPropertiesCorrectly()
    {
        // Arrange
        var correlationId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var log = new OccurrenceLog
        {
            Timestamp = now,
            Level = "Information",
            Message = "Test log message",
            Category = "Test",
            Data = new Dictionary<string, object> { ["key"] = "value" }
        };

        // Act
        var storedLog = new StoredLog
        {
            Id = 1,
            CorrelationId = correlationId,
            WorkerId = "test-worker",
            Log = log,
            CreatedAt = now,
            RetryCount = 2
        };

        // Assert
        storedLog.Id.Should().Be(1);
        storedLog.CorrelationId.Should().Be(correlationId);
        storedLog.WorkerId.Should().Be("test-worker");
        storedLog.Log.Should().NotBeNull();
        storedLog.Log.Message.Should().Be("Test log message");
        storedLog.RetryCount.Should().Be(2);
    }
}

[Trait("SDK Unit Tests", "LocalStoreStats unit tests.")]
public class LocalStoreStatsTests
{
    [Fact]
    public void LocalStoreStats_ShouldInitializeWithDefaultValues()
    {
        // Act
        var stats = new LocalStoreStats();

        // Assert
        stats.PendingStatusUpdates.Should().Be(0);
        stats.PendingLogs.Should().Be(0);
        stats.ActiveJobs.Should().Be(0);
        stats.FinalizedJobs.Should().Be(0);
        stats.OldestPendingRecordAge.Should().BeNull();
        stats.TotalPendingRecords.Should().Be(0);
    }

    [Fact]
    public void LocalStoreStats_TotalPendingRecords_ShouldCalculateCorrectly()
    {
        // Act
        var stats = new LocalStoreStats
        {
            PendingStatusUpdates = 5,
            PendingLogs = 10
        };

        // Assert
        stats.TotalPendingRecords.Should().Be(15);
    }

    [Fact]
    public void LocalStoreStats_ShouldSetPropertiesCorrectly()
    {
        // Act
        var stats = new LocalStoreStats
        {
            PendingStatusUpdates = 5,
            PendingLogs = 10,
            ActiveJobs = 3,
            FinalizedJobs = 100,
            OldestPendingRecordAge = TimeSpan.FromMinutes(30)
        };

        // Assert
        stats.PendingStatusUpdates.Should().Be(5);
        stats.PendingLogs.Should().Be(10);
        stats.ActiveJobs.Should().Be(3);
        stats.FinalizedJobs.Should().Be(100);
        stats.OldestPendingRecordAge.Should().Be(TimeSpan.FromMinutes(30));
        stats.TotalPendingRecords.Should().Be(15);
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
            ["key1"] = "value1",
            ["key2"] = 123
        };

        // Act
        var log = new OccurrenceLog
        {
            Timestamp = now,
            Level = "Error",
            Message = "Test error message",
            Data = data,
            Category = "TestCategory",
            ExceptionType = "InvalidOperationException"
        };

        // Assert
        log.Timestamp.Should().Be(now);
        log.Level.Should().Be("Error");
        log.Message.Should().Be("Test error message");
        log.Data.Should().BeEquivalentTo(data);
        log.Category.Should().Be("TestCategory");
        log.ExceptionType.Should().Be("InvalidOperationException");
    }

    [Fact]
    public void OccurrenceLog_Data_ShouldSupportMultipleTypes()
    {
        // Arrange
        var data = new Dictionary<string, object>
        {
            ["string"] = "text",
            ["int"] = 42,
            ["bool"] = true,
            ["double"] = 3.14,
            ["null"] = null,
            ["array"] = new[] { 1, 2, 3 }
        };

        // Act
        var log = new OccurrenceLog { Data = data };

        // Assert
        log.Data.Should().HaveCount(6);
        log.Data["string"].Should().Be("text");
        log.Data["int"].Should().Be(42);
        log.Data["bool"].Should().Be(true);
    }
}

[Trait("SDK Unit Tests", "JobStatusUpdateMessage unit tests.")]
public class JobStatusUpdateMessageTests
{
    [Fact]
    public void JobStatusUpdateMessage_ShouldInitializeWithDefaults()
    {
        // Act
        var message = new JobStatusUpdateMessage();

        // Assert
        message.CorrelationId.Should().Be(Guid.Empty);
        message.JobId.Should().Be(Guid.Empty);
        message.WorkerId.Should().BeNull();
        message.Status.Should().Be(JobOccurrenceStatus.Queued);
        message.StartTime.Should().BeNull();
        message.EndTime.Should().BeNull();
        message.DurationMs.Should().BeNull();
        message.Result.Should().BeNull();
        message.Exception.Should().BeNull();
        message.MessageTimestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void JobStatusUpdateMessage_ShouldSetPropertiesCorrectly()
    {
        // Arrange
        var correlationId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // Act
        var message = new JobStatusUpdateMessage
        {
            CorrelationId = correlationId,
            JobId = jobId,
            WorkerId = "test-worker",
            Status = JobOccurrenceStatus.Completed,
            StartTime = now.AddMinutes(-5),
            EndTime = now,
            DurationMs = 300000,
            Result = "Success",
            Exception = null,
            MessageTimestamp = now
        };

        // Assert
        message.CorrelationId.Should().Be(correlationId);
        message.JobId.Should().Be(jobId);
        message.WorkerId.Should().Be("test-worker");
        message.Status.Should().Be(JobOccurrenceStatus.Completed);
        message.DurationMs.Should().Be(300000);
        message.Result.Should().Be("Success");
    }
}
