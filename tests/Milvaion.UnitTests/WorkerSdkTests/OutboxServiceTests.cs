using FluentAssertions;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Domain.JsonModels;
using Milvasoft.Milvaion.Sdk.Worker.Persistence;
using Milvasoft.Milvaion.Sdk.Worker.RabbitMQ;
using Moq;

namespace Milvaion.UnitTests.WorkerSdkTests;

[Trait("SDK Unit Tests", "OutboxService unit tests.")]
public class OutboxServiceTests
{
    private readonly Mock<ILocalStateStore> _localStoreMock;
    private readonly Mock<IStatusUpdatePublisher> _statusPublisherMock;
    private readonly Mock<ILogPublisher> _logPublisherMock;
    private readonly Mock<IConnectionMonitor> _connectionMonitorMock;
    private readonly Mock<IMilvaLogger> _loggerMock;

    public OutboxServiceTests()
    {
        _localStoreMock = new Mock<ILocalStateStore>();
        _statusPublisherMock = new Mock<IStatusUpdatePublisher>();
        _logPublisherMock = new Mock<ILogPublisher>();
        _connectionMonitorMock = new Mock<IConnectionMonitor>();
        _loggerMock = new Mock<IMilvaLogger>();
    }

    [Fact]
    public void GetLocalStore_ShouldReturnLocalStateStore()
    {
        // Arrange
        var outboxService = CreateOutboxService(isRabbitMQHealthy: true);

        // Act
        var localStore = outboxService.GetLocalStore();

        // Assert
        localStore.Should().NotBeNull();
    }

    [Fact]
    public async Task PublishStatusUpdateAsync_ShouldPublishToRabbitMQ_WhenConnectionIsHealthy()
    {
        // Arrange
        var correlationId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var workerId = "test-worker";
        var instanceId = "test-worker-01";
        var status = JobOccurrenceStatus.Running;

        _connectionMonitorMock.Setup(x => x.IsRabbitMQHealthy).Returns(true);

        _statusPublisherMock.Setup(x => x.PublishStatusAsync(
            It.IsAny<Guid>(),
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<JobOccurrenceStatus>(),
            It.IsAny<DateTime?>(),
            It.IsAny<DateTime?>(),
            It.IsAny<long?>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var outboxService = CreateOutboxService(isRabbitMQHealthy: true);

        // Act
        await outboxService.PublishStatusUpdateAsync(
            correlationId,
            jobId,
            workerId,
            instanceId,
            status,
            cancellationToken: CancellationToken.None);

        // Assert
        _statusPublisherMock.Verify(x => x.PublishStatusAsync(
            correlationId,
            jobId,
            workerId,
            instanceId,
            status,
            It.IsAny<DateTime?>(),
            It.IsAny<DateTime?>(),
            It.IsAny<long?>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishStatusUpdateAsync_ShouldStoreLocally_WhenConnectionIsUnhealthy()
    {
        // Arrange
        var correlationId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var workerId = "test-worker";
        var instanceId = "test-worker-01";
        var status = JobOccurrenceStatus.Running;

        _connectionMonitorMock.Setup(x => x.IsRabbitMQHealthy).Returns(false);

        _localStoreMock.Setup(x => x.StoreStatusUpdateAsync(
            It.IsAny<Guid>(),
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<JobOccurrenceStatus>(),
            It.IsAny<DateTime?>(),
            It.IsAny<DateTime?>(),
            It.IsAny<long?>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var outboxService = CreateOutboxService(isRabbitMQHealthy: false);

        // Act
        await outboxService.PublishStatusUpdateAsync(
            correlationId,
            jobId,
            workerId,
            instanceId,
            status,
            cancellationToken: CancellationToken.None);

        // Assert
        _localStoreMock.Verify(x => x.StoreStatusUpdateAsync(
            correlationId,
            jobId,
            workerId,
            instanceId,
            status,
            It.IsAny<DateTime?>(),
            It.IsAny<DateTime?>(),
            It.IsAny<long?>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishStatusUpdateAsync_ShouldStoreLocally_WhenRabbitMQPublishFails()
    {
        // Arrange
        var correlationId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var workerId = "test-worker";
        var instanceId = "test-worker-01";
        var status = JobOccurrenceStatus.Completed;

        _connectionMonitorMock.Setup(x => x.IsRabbitMQHealthy).Returns(true);

        _statusPublisherMock.Setup(x => x.PublishStatusAsync(
            It.IsAny<Guid>(),
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<JobOccurrenceStatus>(),
            It.IsAny<DateTime?>(),
            It.IsAny<DateTime?>(),
            It.IsAny<long?>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>())).ThrowsAsync(new Exception("RabbitMQ connection failed"));

        _localStoreMock.Setup(x => x.StoreStatusUpdateAsync(
            It.IsAny<Guid>(),
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<JobOccurrenceStatus>(),
            It.IsAny<DateTime?>(),
            It.IsAny<DateTime?>(),
            It.IsAny<long?>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var outboxService = CreateOutboxService(isRabbitMQHealthy: true);

        // Act
        await outboxService.PublishStatusUpdateAsync(
            correlationId,
            jobId,
            workerId,
            instanceId,
            status,
            cancellationToken: CancellationToken.None);

        // Assert
        _localStoreMock.Verify(x => x.StoreStatusUpdateAsync(
            correlationId,
            jobId,
            workerId,
            instanceId,
            status,
            It.IsAny<DateTime?>(),
            It.IsAny<DateTime?>(),
            It.IsAny<long?>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishLogAsync_ShouldPublishToRabbitMQ_WhenConnectionIsHealthy()
    {
        // Arrange
        var correlationId = Guid.CreateVersion7();
        var workerId = "test-worker";
        var log = new OccurrenceLog
        {
            Timestamp = DateTime.UtcNow,
            Level = "Information",
            Message = "Test log message",
            Category = "Test"
        };

        _connectionMonitorMock.Setup(x => x.IsRabbitMQHealthy).Returns(true);

        _logPublisherMock.Setup(x => x.PublishLogAsync(
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<OccurrenceLog>(),
            It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var outboxService = CreateOutboxService(isRabbitMQHealthy: true);

        // Act
        await outboxService.PublishLogAsync(correlationId, workerId, log, CancellationToken.None);

        // Assert
        _logPublisherMock.Verify(x => x.PublishLogAsync(
            correlationId,
            workerId,
            log,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishLogAsync_ShouldStoreLocally_WhenConnectionIsUnhealthy()
    {
        // Arrange
        var correlationId = Guid.CreateVersion7();
        var workerId = "test-worker";
        var log = new OccurrenceLog
        {
            Timestamp = DateTime.UtcNow,
            Level = "Error",
            Message = "Test error log",
            Category = "Test"
        };

        _connectionMonitorMock.Setup(x => x.IsRabbitMQHealthy).Returns(false);

        _localStoreMock.Setup(x => x.StoreLogAsync(
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<OccurrenceLog>(),
            It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var outboxService = CreateOutboxService(isRabbitMQHealthy: false);

        // Act
        await outboxService.PublishLogAsync(correlationId, workerId, log, CancellationToken.None);

        // Assert
        _localStoreMock.Verify(x => x.StoreLogAsync(
            correlationId,
            workerId,
            log,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncStatusUpdatesAsync_ShouldReturnSkipped_WhenRabbitMQIsUnhealthy()
    {
        // Arrange
        _connectionMonitorMock.Setup(x => x.IsRabbitMQHealthy).Returns(false);

        var outboxService = CreateOutboxService(isRabbitMQHealthy: false);

        // Act
        var result = await outboxService.SyncStatusUpdatesAsync();

        // Assert
        result.Skipped.Should().BeTrue();
        result.Message.Should().Contain("unhealthy");
    }

    [Fact]
    public async Task SyncStatusUpdatesAsync_ShouldReturnSuccess_WhenNoPendingUpdates()
    {
        // Arrange
        _connectionMonitorMock.Setup(x => x.IsRabbitMQHealthy).Returns(true);

        _localStoreMock.Setup(x => x.GetPendingStatusUpdatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync([]);

        var outboxService = CreateOutboxService(isRabbitMQHealthy: true);

        // Act
        var result = await outboxService.SyncStatusUpdatesAsync();

        // Assert
        result.Success.Should().BeTrue();
        result.Message.Should().Contain("No pending");
    }

    [Fact]
    public async Task SyncStatusUpdatesAsync_ShouldSyncPendingUpdates_WhenUpdatesExist()
    {
        // Arrange
        var pendingUpdates = new List<StoredStatusUpdate>
        {
            new()
            {
                Id = 1,
                OccurrenceId = Guid.CreateVersion7(),
                JobId = Guid.CreateVersion7(),
                WorkerId = "test-worker",
                Status = JobOccurrenceStatus.Completed,
                RetryCount = 0
            },
            new()
            {
                Id = 2,
                OccurrenceId = Guid.CreateVersion7(),
                JobId = Guid.CreateVersion7(),
                WorkerId = "test-worker",
                Status = JobOccurrenceStatus.Failed,
                RetryCount = 0
            }
        };

        _connectionMonitorMock.Setup(x => x.IsRabbitMQHealthy).Returns(true);

        _localStoreMock.Setup(x => x.GetPendingStatusUpdatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(pendingUpdates);

        _statusPublisherMock.Setup(x => x.PublishStatusAsync(
            It.IsAny<Guid>(),
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<JobOccurrenceStatus>(),
            It.IsAny<DateTime?>(),
            It.IsAny<DateTime?>(),
            It.IsAny<long?>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        _localStoreMock.Setup(x => x.MarkStatusUpdateAsSyncedAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
                       .Returns(Task.CompletedTask);

        var outboxService = CreateOutboxService(isRabbitMQHealthy: true);

        // Act
        var result = await outboxService.SyncStatusUpdatesAsync();

        // Assert
        result.Success.Should().BeTrue();
        result.SyncedCount.Should().Be(2);
        _localStoreMock.Verify(x => x.MarkStatusUpdateAsSyncedAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task SyncStatusUpdatesAsync_ShouldSkipAndMarkAsSynced_WhenMaxRetriesExceeded()
    {
        // Arrange
        var pendingUpdates = new List<StoredStatusUpdate>
        {
            new()
            {
                Id = 1,
                OccurrenceId = Guid.CreateVersion7(),
                JobId = Guid.CreateVersion7(),
                WorkerId = "test-worker",
                Status = JobOccurrenceStatus.Completed,
                RetryCount = 5 // Exceeds max retries (default 3)
            }
        };

        _connectionMonitorMock.Setup(x => x.IsRabbitMQHealthy).Returns(true);

        _localStoreMock.Setup(x => x.GetPendingStatusUpdatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(pendingUpdates);

        _localStoreMock.Setup(x => x.MarkStatusUpdateAsSyncedAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
                       .Returns(Task.CompletedTask);

        var outboxService = CreateOutboxService(isRabbitMQHealthy: true);

        // Act
        var result = await outboxService.SyncStatusUpdatesAsync();

        // Assert
        result.FailedCount.Should().Be(1);
        _localStoreMock.Verify(x => x.MarkStatusUpdateAsSyncedAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        _statusPublisherMock.Verify(x => x.PublishStatusAsync(
            It.IsAny<Guid>(),
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<JobOccurrenceStatus>(),
            It.IsAny<DateTime?>(),
            It.IsAny<DateTime?>(),
            It.IsAny<long?>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncLogsAsync_ShouldReturnSkipped_WhenRabbitMQIsUnhealthy()
    {
        // Arrange
        _connectionMonitorMock.Setup(x => x.IsRabbitMQHealthy).Returns(false);

        var outboxService = CreateOutboxService(isRabbitMQHealthy: false);

        // Act
        var result = await outboxService.SyncLogsAsync();

        // Assert
        result.Skipped.Should().BeTrue();
        result.Message.Should().Contain("unhealthy");
    }

    [Fact]
    public async Task SyncLogsAsync_ShouldReturnSuccess_WhenNoPendingLogs()
    {
        // Arrange
        _connectionMonitorMock.Setup(x => x.IsRabbitMQHealthy).Returns(true);

        _localStoreMock.Setup(x => x.GetPendingLogsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync([]);

        var outboxService = CreateOutboxService(isRabbitMQHealthy: true);

        // Act
        var result = await outboxService.SyncLogsAsync();

        // Assert
        result.Success.Should().BeTrue();
        result.Message.Should().Contain("No pending");
    }

    [Fact]
    public async Task SyncLogsAsync_ShouldSyncPendingLogs_WhenLogsExist()
    {
        // Arrange
        var pendingLogs = new List<StoredLog>
        {
            new()
            {
                Id = 1,
                OccurrenceId = Guid.CreateVersion7(),
                WorkerId = "test-worker",
                Log = new OccurrenceLog { Level = "Information", Message = "Test log 1", Timestamp = DateTime.UtcNow },
                RetryCount = 0
            },
            new()
            {
                Id = 2,
                OccurrenceId = Guid.CreateVersion7(),
                WorkerId = "test-worker",
                Log = new OccurrenceLog { Level = "Warning", Message = "Test log 2", Timestamp = DateTime.UtcNow },
                RetryCount = 0
            }
        };

        _connectionMonitorMock.Setup(x => x.IsRabbitMQHealthy).Returns(true);

        _localStoreMock.Setup(x => x.GetPendingLogsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(pendingLogs);

        _logPublisherMock.Setup(x => x.PublishLogAsync(
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<OccurrenceLog>(),
            It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        _localStoreMock.Setup(x => x.MarkLogAsSyncedAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
                       .Returns(Task.CompletedTask);

        var outboxService = CreateOutboxService(isRabbitMQHealthy: true);

        // Act
        var result = await outboxService.SyncLogsAsync();

        // Assert
        result.Success.Should().BeTrue();
        result.SyncedCount.Should().Be(2);
        _localStoreMock.Verify(x => x.MarkLogAsSyncedAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    #region PublishJobHeartbeatAsync

    [Fact]
    public async Task PublishJobHeartbeatAsync_ShouldPublishRunningStatus_WhenConnectionIsHealthy()
    {
        // Arrange
        var correlationId = Guid.CreateVersion7();
        var jobId = Guid.CreateVersion7();
        var workerId = "test-worker";
        var instanceId = "test-worker-01";

        _connectionMonitorMock.Setup(x => x.IsRabbitMQHealthy).Returns(true);

        _statusPublisherMock.Setup(x => x.PublishStatusAsync(
            It.IsAny<Guid>(),
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<JobOccurrenceStatus>(),
            It.IsAny<DateTime?>(),
            It.IsAny<DateTime?>(),
            It.IsAny<long?>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var outboxService = CreateOutboxService(isRabbitMQHealthy: true);

        // Act
        await outboxService.PublishJobHeartbeatAsync(correlationId, jobId, workerId, instanceId);

        // Assert
        _statusPublisherMock.Verify(x => x.PublishStatusAsync(
            correlationId,
            jobId,
            workerId,
            instanceId,
            JobOccurrenceStatus.Running,
            It.IsAny<DateTime?>(),
            It.IsAny<DateTime?>(),
            It.IsAny<long?>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishJobHeartbeatAsync_ShouldSkip_WhenConnectionIsUnhealthy()
    {
        // Arrange
        var outboxService = CreateOutboxService(isRabbitMQHealthy: false);

        // Act
        await outboxService.PublishJobHeartbeatAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), "worker", "instance");

        // Assert
        _statusPublisherMock.Verify(x => x.PublishStatusAsync(
            It.IsAny<Guid>(),
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<JobOccurrenceStatus>(),
            It.IsAny<DateTime?>(),
            It.IsAny<DateTime?>(),
            It.IsAny<long?>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PublishJobHeartbeatAsync_ShouldNotThrow_WhenPublishFails()
    {
        // Arrange
        _connectionMonitorMock.Setup(x => x.IsRabbitMQHealthy).Returns(true);

        _statusPublisherMock.Setup(x => x.PublishStatusAsync(
            It.IsAny<Guid>(),
            It.IsAny<Guid>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<JobOccurrenceStatus>(),
            It.IsAny<DateTime?>(),
            It.IsAny<DateTime?>(),
            It.IsAny<long?>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>())).ThrowsAsync(new Exception("Connection lost"));

        var outboxService = CreateOutboxService(isRabbitMQHealthy: true);

        // Act & Assert - Should not throw
        var act = () => outboxService.PublishJobHeartbeatAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), "worker", "instance");
        await act.Should().NotThrowAsync();
    }

    #endregion

    #region SyncStatusUpdatesAsync - Negative Scenarios

    [Fact]
    public async Task SyncStatusUpdatesAsync_ShouldHandlePartialFailure_WhenSomePublishFails()
    {
        // Arrange
        var pendingUpdates = new List<StoredStatusUpdate>
        {
            new() { Id = 1, OccurrenceId = Guid.CreateVersion7(), JobId = Guid.CreateVersion7(), WorkerId = "w", Status = JobOccurrenceStatus.Completed, RetryCount = 0 },
            new() { Id = 2, OccurrenceId = Guid.CreateVersion7(), JobId = Guid.CreateVersion7(), WorkerId = "w", Status = JobOccurrenceStatus.Failed, RetryCount = 0 }
        };

        _connectionMonitorMock.Setup(x => x.IsRabbitMQHealthy).Returns(true);

        _localStoreMock.Setup(x => x.GetPendingStatusUpdatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(pendingUpdates);

        // First publish succeeds, second fails
        var callCount = 0;
        _statusPublisherMock.Setup(x => x.PublishStatusAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<JobOccurrenceStatus>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<long?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 1 ? Task.CompletedTask : Task.FromException(new Exception("Publish failed"));
            });

        _localStoreMock.Setup(x => x.MarkStatusUpdateAsSyncedAsync(It.IsAny<long>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _localStoreMock.Setup(x => x.IncrementStatusUpdateRetryAsync(It.IsAny<long>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var outboxService = CreateOutboxService(isRabbitMQHealthy: true);

        // Act
        var result = await outboxService.SyncStatusUpdatesAsync();

        // Assert
        result.Success.Should().BeTrue("at least one update was synced");
        result.SyncedCount.Should().Be(1);
        result.FailedCount.Should().Be(1);
        _localStoreMock.Verify(x => x.IncrementStatusUpdateRetryAsync(2, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncStatusUpdatesAsync_ShouldReturnFailed_WhenAllPublishFail()
    {
        // Arrange
        var pendingUpdates = new List<StoredStatusUpdate>
        {
            new() { Id = 1, OccurrenceId = Guid.CreateVersion7(), JobId = Guid.CreateVersion7(), WorkerId = "w", Status = JobOccurrenceStatus.Running, RetryCount = 0 }
        };

        _connectionMonitorMock.Setup(x => x.IsRabbitMQHealthy).Returns(true);

        _localStoreMock.Setup(x => x.GetPendingStatusUpdatesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(pendingUpdates);

        _statusPublisherMock.Setup(x => x.PublishStatusAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<JobOccurrenceStatus>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(),
            It.IsAny<long?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Connection lost"));

        _localStoreMock.Setup(x => x.IncrementStatusUpdateRetryAsync(It.IsAny<long>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var outboxService = CreateOutboxService(isRabbitMQHealthy: true);

        // Act
        var result = await outboxService.SyncStatusUpdatesAsync();

        // Assert
        result.SyncedCount.Should().Be(0);
        result.FailedCount.Should().Be(1);
    }

    #endregion

    #region SyncLogsAsync - Negative Scenarios

    [Fact]
    public async Task SyncLogsAsync_ShouldSkipAndMarkAsSynced_WhenMaxRetriesExceeded()
    {
        // Arrange
        var pendingLogs = new List<StoredLog>
        {
            new()
            {
                Id = 1,
                OccurrenceId = Guid.CreateVersion7(),
                WorkerId = "test-worker",
                Log = new OccurrenceLog { Level = "Error", Message = "Stuck log", Timestamp = DateTime.UtcNow },
                RetryCount = 5
            }
        };

        _connectionMonitorMock.Setup(x => x.IsRabbitMQHealthy).Returns(true);

        _localStoreMock.Setup(x => x.GetPendingLogsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(pendingLogs);

        _localStoreMock.Setup(x => x.MarkLogAsSyncedAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
                       .Returns(Task.CompletedTask);

        var outboxService = CreateOutboxService(isRabbitMQHealthy: true);

        // Act
        var result = await outboxService.SyncLogsAsync();

        // Assert
        result.FailedCount.Should().Be(1);
        _localStoreMock.Verify(x => x.MarkLogAsSyncedAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        _logPublisherMock.Verify(x => x.PublishLogAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<OccurrenceLog>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncLogsAsync_ShouldIncrementRetry_WhenPublishFails()
    {
        // Arrange
        var pendingLogs = new List<StoredLog>
        {
            new()
            {
                Id = 1,
                OccurrenceId = Guid.CreateVersion7(),
                WorkerId = "test-worker",
                Log = new OccurrenceLog { Level = "Error", Message = "Failing log", Timestamp = DateTime.UtcNow },
                RetryCount = 0
            }
        };

        _connectionMonitorMock.Setup(x => x.IsRabbitMQHealthy).Returns(true);

        _localStoreMock.Setup(x => x.GetPendingLogsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                       .ReturnsAsync(pendingLogs);

        _logPublisherMock.Setup(x => x.PublishLogAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<OccurrenceLog>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("RabbitMQ down"));

        _localStoreMock.Setup(x => x.IncrementLogRetryAsync(It.IsAny<long>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var outboxService = CreateOutboxService(isRabbitMQHealthy: true);

        // Act
        var result = await outboxService.SyncLogsAsync();

        // Assert
        result.FailedCount.Should().Be(1);
        _localStoreMock.Verify(x => x.IncrementLogRetryAsync(1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishLogAsync_ShouldStoreLocally_WhenRabbitMQPublishFails()
    {
        // Arrange
        var correlationId = Guid.CreateVersion7();
        var workerId = "test-worker";
        var log = new OccurrenceLog
        {
            Timestamp = DateTime.UtcNow,
            Level = "Error",
            Message = "Test error log"
        };

        _connectionMonitorMock.Setup(x => x.IsRabbitMQHealthy).Returns(true);

        _logPublisherMock.Setup(x => x.PublishLogAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<OccurrenceLog>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Connection refused"));

        _localStoreMock.Setup(x => x.StoreLogAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<OccurrenceLog>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var outboxService = CreateOutboxService(isRabbitMQHealthy: true);

        // Act
        await outboxService.PublishLogAsync(correlationId, workerId, log, CancellationToken.None);

        // Assert - Should fallback to local store
        _localStoreMock.Verify(x => x.StoreLogAsync(
            correlationId, workerId, log, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Offline resilience disabled

    [Fact]
    public async Task PublishStatusUpdateAsync_ShouldStillPublish_WhenResilienceDisabledAndMonitorUnhealthy()
    {
        // Arrange
        _connectionMonitorMock.Setup(x => x.IsRabbitMQHealthy).Returns(false);

        var outboxService = new OutboxService(new NullLocalStateStore(), _statusPublisherMock.Object, _logPublisherMock.Object, _connectionMonitorMock.Object, _loggerMock.Object);

        var occurrenceId = Guid.CreateVersion7();

        // Act
        await outboxService.PublishStatusUpdateAsync(occurrenceId, Guid.CreateVersion7(), "worker", "instance", JobOccurrenceStatus.Completed);

        // Assert
        _statusPublisherMock.Verify(x => x.PublishStatusAsync(occurrenceId, It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), JobOccurrenceStatus.Completed, It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<long?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PublishLogAsync_ShouldStillPublish_WhenResilienceDisabledAndMonitorUnhealthy()
    {
        // Arrange
        _connectionMonitorMock.Setup(x => x.IsRabbitMQHealthy).Returns(false);

        var outboxService = new OutboxService(new NullLocalStateStore(), _statusPublisherMock.Object, _logPublisherMock.Object, _connectionMonitorMock.Object, _loggerMock.Object);

        var log = new OccurrenceLog { Timestamp = DateTime.UtcNow, Level = "Information", Message = "m" };

        // Act
        await outboxService.PublishLogAsync(Guid.CreateVersion7(), "worker", log);

        // Assert
        _logPublisherMock.Verify(x => x.PublishLogAsync(It.IsAny<Guid>(), "worker", log, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    private OutboxService CreateOutboxService(bool isRabbitMQHealthy)
    {
        _connectionMonitorMock.Setup(x => x.IsRabbitMQHealthy).Returns(isRabbitMQHealthy);

        return new OutboxService(
            _localStoreMock.Object,
            _statusPublisherMock.Object,
            _logPublisherMock.Object,
            _connectionMonitorMock.Object,
            _loggerMock.Object);
    }
}
