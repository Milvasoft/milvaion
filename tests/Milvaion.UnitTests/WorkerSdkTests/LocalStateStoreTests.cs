using FluentAssertions;
using Microsoft.Extensions.Logging;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Domain.JsonModels;
using Milvasoft.Milvaion.Sdk.Worker.Persistence;
using Moq;

namespace Milvaion.UnitTests.WorkerSdkTests;

[Trait("SDK Unit Tests", "LocalStateStore unit tests.")]
public class LocalStateStoreTests : IAsyncDisposable
{
    private readonly string _testDatabasePath;
    private readonly LocalStateStore _localStateStore;
    private readonly Mock<ILoggerFactory> _loggerFactoryMock;

    public LocalStateStoreTests()
    {
        _testDatabasePath = Path.Combine(Path.GetTempPath(), $"LocalStateStoreTests_{Guid.NewGuid()}");
        _loggerFactoryMock = new Mock<ILoggerFactory>();
        _loggerFactoryMock.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

        _localStateStore = new LocalStateStore(_testDatabasePath, _loggerFactoryMock.Object);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1816:Dispose methods should call SuppressFinalize", Justification = "<Pending>")]
    public async ValueTask DisposeAsync()
    {
        await _localStateStore.DisposeAsync();

        if (Directory.Exists(_testDatabasePath))
        {
            try
            {
                Directory.Delete(_testDatabasePath, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public async Task InitializeAsync_ShouldCreateDatabase_WhenCalled()
    {
        // Act
        await _localStateStore.InitializeAsync();

        // Assert
        var dbFile = Path.Combine(_testDatabasePath, "worker.db");
        File.Exists(dbFile).Should().BeTrue();
    }

    [Fact]
    public async Task StoreStatusUpdateAsync_ShouldStoreStatusUpdate_Successfully()
    {
        // Arrange
        await _localStateStore.InitializeAsync();
        var correlationId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var workerId = "test-worker";
        var status = JobOccurrenceStatus.Running;

        // Act
        await _localStateStore.StoreStatusUpdateAsync(
            correlationId,
            jobId,
            workerId,
            status,
            startTime: DateTime.UtcNow,
            cancellationToken: CancellationToken.None);

        // Assert
        var pendingUpdates = await _localStateStore.GetPendingStatusUpdatesAsync(100, CancellationToken.None);
        pendingUpdates.Should().ContainSingle();
        pendingUpdates[0].CorrelationId.Should().Be(correlationId);
        pendingUpdates[0].JobId.Should().Be(jobId);
        pendingUpdates[0].WorkerId.Should().Be(workerId);
        pendingUpdates[0].Status.Should().Be(status);
    }

    [Fact]
    public async Task GetPendingStatusUpdatesAsync_ShouldReturnEmptyList_WhenNoUpdatesExist()
    {
        // Arrange
        await _localStateStore.InitializeAsync();

        // Act
        var pendingUpdates = await _localStateStore.GetPendingStatusUpdatesAsync(100, CancellationToken.None);

        // Assert
        pendingUpdates.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPendingStatusUpdatesAsync_ShouldLimitResults_WhenMaxCountIsSpecified()
    {
        // Arrange
        await _localStateStore.InitializeAsync();

        for (int i = 0; i < 10; i++)
        {
            await _localStateStore.StoreStatusUpdateAsync(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "test-worker",
                JobOccurrenceStatus.Running,
                cancellationToken: CancellationToken.None);
        }

        // Act
        var pendingUpdates = await _localStateStore.GetPendingStatusUpdatesAsync(5, CancellationToken.None);

        // Assert
        pendingUpdates.Should().HaveCount(5);
    }

    [Fact]
    public async Task MarkStatusUpdateAsSyncedAsync_ShouldDeleteRecord_WhenCalled()
    {
        // Arrange
        await _localStateStore.InitializeAsync();
        var correlationId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        await _localStateStore.StoreStatusUpdateAsync(
            correlationId,
            jobId,
            "test-worker",
            JobOccurrenceStatus.Completed,
            cancellationToken: CancellationToken.None);

        var pendingUpdates = await _localStateStore.GetPendingStatusUpdatesAsync(100, CancellationToken.None);
        var updateId = pendingUpdates[0].Id;

        // Act
        await _localStateStore.MarkStatusUpdateAsSyncedAsync(updateId, CancellationToken.None);

        // Assert
        var remainingUpdates = await _localStateStore.GetPendingStatusUpdatesAsync(100, CancellationToken.None);
        remainingUpdates.Should().BeEmpty();
    }

    [Fact]
    public async Task IncrementStatusUpdateRetryAsync_ShouldIncrementRetryCount()
    {
        // Arrange
        await _localStateStore.InitializeAsync();
        var correlationId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        await _localStateStore.StoreStatusUpdateAsync(
            correlationId,
            jobId,
            "test-worker",
            JobOccurrenceStatus.Running,
            cancellationToken: CancellationToken.None);

        var pendingUpdates = await _localStateStore.GetPendingStatusUpdatesAsync(100, CancellationToken.None);
        var updateId = pendingUpdates[0].Id;
        pendingUpdates[0].RetryCount.Should().Be(0);

        // Act
        await _localStateStore.IncrementStatusUpdateRetryAsync(updateId, CancellationToken.None);

        // Assert
        var updatedPendingUpdates = await _localStateStore.GetPendingStatusUpdatesAsync(100, CancellationToken.None);
        updatedPendingUpdates[0].RetryCount.Should().Be(1);
    }

    [Fact]
    public async Task StoreLogAsync_ShouldStoreLog_Successfully()
    {
        // Arrange
        await _localStateStore.InitializeAsync();
        var correlationId = Guid.NewGuid();
        var workerId = "test-worker";
        var log = new OccurrenceLog
        {
            Timestamp = DateTime.UtcNow,
            Level = "Information",
            Message = "Test log message",
            Category = "Test"
        };

        // Act
        await _localStateStore.StoreLogAsync(correlationId, workerId, log, CancellationToken.None);

        // Assert
        var pendingLogs = await _localStateStore.GetPendingLogsAsync(100, CancellationToken.None);
        pendingLogs.Should().ContainSingle();
        pendingLogs[0].CorrelationId.Should().Be(correlationId);
        pendingLogs[0].WorkerId.Should().Be(workerId);
        pendingLogs[0].Log.Message.Should().Be("Test log message");
    }

    [Fact]
    public async Task GetPendingLogsAsync_ShouldReturnEmptyList_WhenNoLogsExist()
    {
        // Arrange
        await _localStateStore.InitializeAsync();

        // Act
        var pendingLogs = await _localStateStore.GetPendingLogsAsync(100, CancellationToken.None);

        // Assert
        pendingLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task MarkLogAsSyncedAsync_ShouldDeleteRecord_WhenCalled()
    {
        // Arrange
        await _localStateStore.InitializeAsync();
        var correlationId = Guid.NewGuid();
        var log = new OccurrenceLog
        {
            Timestamp = DateTime.UtcNow,
            Level = "Information",
            Message = "Test log"
        };

        await _localStateStore.StoreLogAsync(correlationId, "test-worker", log, CancellationToken.None);

        var pendingLogs = await _localStateStore.GetPendingLogsAsync(100, CancellationToken.None);
        var logId = pendingLogs[0].Id;

        // Act
        await _localStateStore.MarkLogAsSyncedAsync(logId, CancellationToken.None);

        // Assert
        var remainingLogs = await _localStateStore.GetPendingLogsAsync(100, CancellationToken.None);
        remainingLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task IncrementLogRetryAsync_ShouldIncrementRetryCount()
    {
        // Arrange
        await _localStateStore.InitializeAsync();
        var correlationId = Guid.NewGuid();
        var log = new OccurrenceLog
        {
            Timestamp = DateTime.UtcNow,
            Level = "Error",
            Message = "Test error log"
        };

        await _localStateStore.StoreLogAsync(correlationId, "test-worker", log, CancellationToken.None);

        var pendingLogs = await _localStateStore.GetPendingLogsAsync(100, CancellationToken.None);
        var logId = pendingLogs[0].Id;
        pendingLogs[0].RetryCount.Should().Be(0);

        // Act
        await _localStateStore.IncrementLogRetryAsync(logId, CancellationToken.None);

        // Assert
        var updatedPendingLogs = await _localStateStore.GetPendingLogsAsync(100, CancellationToken.None);
        updatedPendingLogs[0].RetryCount.Should().Be(1);
    }

    [Fact]
    public async Task RecordJobStartAsync_ShouldRecordJobExecution()
    {
        // Arrange
        await _localStateStore.InitializeAsync();
        var correlationId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var jobType = "TestJob";
        var workerId = "test-worker";

        // Act
        await _localStateStore.RecordJobStartAsync(correlationId, jobId, jobType, workerId, CancellationToken.None);

        // Assert
        var isFinalized = await _localStateStore.IsJobFinalizedAsync(correlationId, CancellationToken.None);
        isFinalized.Should().BeFalse();
    }

    [Fact]
    public async Task FinalizeJobAsync_ShouldMarkJobAsFinalized()
    {
        // Arrange
        await _localStateStore.InitializeAsync();
        var correlationId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var jobType = "TestJob";
        var workerId = "test-worker";

        await _localStateStore.RecordJobStartAsync(correlationId, jobId, jobType, workerId, CancellationToken.None);

        // Act
        await _localStateStore.FinalizeJobAsync(correlationId, JobOccurrenceStatus.Completed, CancellationToken.None);

        // Assert
        var isFinalized = await _localStateStore.IsJobFinalizedAsync(correlationId, CancellationToken.None);
        isFinalized.Should().BeTrue();
    }

    [Fact]
    public async Task IsJobFinalizedAsync_ShouldReturnFalse_WhenJobNotExists()
    {
        // Arrange
        await _localStateStore.InitializeAsync();
        var correlationId = Guid.NewGuid();

        // Act
        var isFinalized = await _localStateStore.IsJobFinalizedAsync(correlationId, CancellationToken.None);

        // Assert
        isFinalized.Should().BeFalse();
    }

    [Fact]
    public async Task GetStatsAsync_ShouldReturnCorrectStatistics()
    {
        // Arrange
        await _localStateStore.InitializeAsync();

        // Add some status updates
        await _localStateStore.StoreStatusUpdateAsync(Guid.NewGuid(), Guid.NewGuid(), "worker", JobOccurrenceStatus.Running, cancellationToken: CancellationToken.None);
        await _localStateStore.StoreStatusUpdateAsync(Guid.NewGuid(), Guid.NewGuid(), "worker", JobOccurrenceStatus.Completed, cancellationToken: CancellationToken.None);

        // Add some logs
        var log = new OccurrenceLog { Timestamp = DateTime.UtcNow, Level = "Info", Message = "Test" };
        await _localStateStore.StoreLogAsync(Guid.NewGuid(), "worker", log, CancellationToken.None);

        // Add a job execution
        var correlationId = Guid.NewGuid();
        await _localStateStore.RecordJobStartAsync(correlationId, Guid.NewGuid(), "TestJob", "worker", CancellationToken.None);

        // Act
        var stats = await _localStateStore.GetStatsAsync(CancellationToken.None);

        // Assert
        stats.PendingStatusUpdates.Should().Be(2);
        stats.PendingLogs.Should().Be(1);
        stats.ActiveJobs.Should().Be(1);
        stats.FinalizedJobs.Should().Be(0);
        stats.TotalPendingRecords.Should().Be(3);
    }

    [Fact]
    public async Task CleanupSyncedRecordsAsync_ShouldRemoveOldRecords()
    {
        // Arrange
        await _localStateStore.InitializeAsync();

        // Add a status update
        await _localStateStore.StoreStatusUpdateAsync(Guid.NewGuid(), Guid.NewGuid(), "worker", JobOccurrenceStatus.Completed, cancellationToken: CancellationToken.None);

        // Add a job execution and finalize it
        var correlationId = Guid.NewGuid();
        await _localStateStore.RecordJobStartAsync(correlationId, Guid.NewGuid(), "TestJob", "worker", CancellationToken.None);
        await _localStateStore.FinalizeJobAsync(correlationId, JobOccurrenceStatus.Completed, CancellationToken.None);

        // Act
        // Use a very small retention period to immediately cleanup
        await _localStateStore.CleanupSyncedRecordsAsync(TimeSpan.Zero, CancellationToken.None);

        // Assert
        var stats = await _localStateStore.GetStatsAsync(CancellationToken.None);
        // Pending records should be cleaned up because retention is 0
        stats.PendingStatusUpdates.Should().Be(0);
        stats.FinalizedJobs.Should().Be(0);
    }

    [Fact]
    public async Task UpdateJobHeartbeatAsync_ShouldUpdateHeartbeatTimestamp()
    {
        // Arrange
        await _localStateStore.InitializeAsync();
        var correlationId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        await _localStateStore.RecordJobStartAsync(correlationId, jobId, "TestJob", "worker", CancellationToken.None);

        // Act - Should not throw
        await _localStateStore.UpdateJobHeartbeatAsync(correlationId, CancellationToken.None);

        // Assert - Job should still not be finalized
        var isFinalized = await _localStateStore.IsJobFinalizedAsync(correlationId, CancellationToken.None);
        isFinalized.Should().BeFalse();
    }

    [Fact]
    public async Task StoreLogAsync_ShouldStoreLogWithData_Successfully()
    {
        // Arrange
        await _localStateStore.InitializeAsync();
        var correlationId = Guid.NewGuid();
        var log = new OccurrenceLog
        {
            Timestamp = DateTime.UtcNow,
            Level = "Information",
            Message = "Test with data",
            Category = "Test",
            Data = new Dictionary<string, object>
            {
                ["key1"] = "value1",
                ["key2"] = 123
            }
        };

        // Act
        await _localStateStore.StoreLogAsync(correlationId, "worker", log, CancellationToken.None);

        // Assert
        var pendingLogs = await _localStateStore.GetPendingLogsAsync(100, CancellationToken.None);
        pendingLogs.Should().ContainSingle();
        pendingLogs[0].Log.Data.Should().NotBeNull();
        pendingLogs[0].Log.Data.Should().ContainKey("key1");
    }
}
