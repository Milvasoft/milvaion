using FluentAssertions;
using Microsoft.Extensions.Logging;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Worker.Core;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using Milvasoft.Milvaion.Sdk.Worker.Persistence;
using Moq;

namespace Milvaion.UnitTests.WorkerSdkTests;

[Trait("SDK Unit Tests", "JobContext unit tests.")]
public class JobContextTests
{
    private readonly Mock<IMilvaLogger> _loggerMock;
    private readonly Mock<OutboxService> _outboxServiceMock;
    private readonly ScheduledJob _scheduledJob;
    private readonly JobConsumerConfig _jobConsumerConfig;
    private readonly Guid _correlationId;
    private readonly string _workerId;

    public JobContextTests()
    {
        _loggerMock = new Mock<IMilvaLogger>();
        _outboxServiceMock = new Mock<OutboxService>(MockBehavior.Loose, null, null, null, null, null);
        _correlationId = Guid.NewGuid();
        _workerId = "test-worker";

        _scheduledJob = new ScheduledJob
        {
            Id = Guid.NewGuid(),
            JobNameInWorker = "TestJob",
            DisplayName = "Test Job",
            Description = "Test job for unit testing",
            IsActive = true,
            ExecuteAt = DateTime.UtcNow,
            JobData = "{\"key\": \"value\"}"
        };

        _jobConsumerConfig = new JobConsumerConfig
        {
            ExecutionTimeoutSeconds = 60,
            MaxRetries = 3,
            LogUserFriendlyLogsViaLogger = false
        };
    }

    [Fact]
    public void Constructor_ShouldInitializePropertiesCorrectly()
    {
        // Act
        var context = new JobContext(
            _correlationId,
            _scheduledJob,
            _workerId,
            _loggerMock.Object,
            _outboxServiceMock.Object,
            _jobConsumerConfig,
            CancellationToken.None);

        // Assert
        context.CorrelationId.Should().Be(_correlationId);
        context.Job.Should().Be(_scheduledJob);
        context.WorkerId.Should().Be(_workerId);
        context.Logger.Should().Be(_loggerMock.Object);
        context.ExecutorJobConsumerConfig.Should().Be(_jobConsumerConfig);
        context.CancellationToken.Should().Be(CancellationToken.None);
    }

    [Fact]
    public void LogInformation_ShouldAddLogToCollection()
    {
        // Arrange
        var context = CreateJobContext();

        // Act
        context.LogInformation("Test information message");

        // Assert
        var logs = context.GetLogs();
        logs.Should().ContainSingle();
        logs[0].Level.Should().Be("Information");
        logs[0].Message.Should().Be("Test information message");
        logs[0].Category.Should().Be("UserCode");
    }

    [Fact]
    public void LogWarning_ShouldAddLogToCollection()
    {
        // Arrange
        var context = CreateJobContext();

        // Act
        context.LogWarning("Test warning message");

        // Assert
        var logs = context.GetLogs();
        logs.Should().ContainSingle();
        logs[0].Level.Should().Be("Warning");
        logs[0].Message.Should().Be("Test warning message");
    }

    [Fact]
    public void LogError_ShouldAddLogWithExceptionDetails()
    {
        // Arrange
        var context = CreateJobContext();
        var exception = new InvalidOperationException("Test exception");

        // Act
        context.LogError("Test error message", exception);

        // Assert
        var logs = context.GetLogs();
        logs.Should().ContainSingle();
        logs[0].Level.Should().Be("Error");
        logs[0].Message.Should().Be("Test error message");
        logs[0].ExceptionType.Should().Be("InvalidOperationException");
        logs[0].Data.Should().ContainKey("ExceptionType");
        logs[0].Data.Should().ContainKey("StackTrace");
    }

    [Fact]
    public void LogError_ShouldAddLog_WithoutException()
    {
        // Arrange
        var context = CreateJobContext();

        // Act
        context.LogError("Test error without exception");

        // Assert
        var logs = context.GetLogs();
        logs.Should().ContainSingle();
        logs[0].Level.Should().Be("Error");
        logs[0].Message.Should().Be("Test error without exception");
        logs[0].ExceptionType.Should().BeNull();
    }

    [Fact]
    public void Log_ShouldAddLogWithCustomCategory()
    {
        // Arrange
        var context = CreateJobContext();

        // Act
        context.Log(LogLevel.Information, "Test message", category: "CustomCategory");

        // Assert
        var logs = context.GetLogs();
        logs.Should().ContainSingle();
        logs[0].Category.Should().Be("CustomCategory");
    }

    [Fact]
    public void Log_ShouldAddLogWithData()
    {
        // Arrange
        var context = CreateJobContext();
        var data = new Dictionary<string, object>
        {
            ["key1"] = "value1",
            ["key2"] = 123
        };

        // Act
        context.Log(LogLevel.Debug, "Test message with data", data);

        // Assert
        var logs = context.GetLogs();
        logs.Should().ContainSingle();
        logs[0].Data.Should().NotBeNull();
        logs[0].Data.Should().ContainKey("key1");
        logs[0].Data["key1"].Should().Be("value1");
    }

    [Fact]
    public void GetLogs_ShouldReturnCopyOfLogs()
    {
        // Arrange
        var context = CreateJobContext();
        context.LogInformation("Log 1");
        context.LogWarning("Log 2");

        // Act
        var logs1 = context.GetLogs();
        var logs2 = context.GetLogs();

        // Assert
        logs1.Should().HaveCount(2);
        logs2.Should().HaveCount(2);
        logs1.Should().NotBeSameAs(logs2); // Should be different list instances
    }

    [Fact]
    public void MultipleLogs_ShouldBeAddedInOrder()
    {
        // Arrange
        var context = CreateJobContext();

        // Act
        context.LogInformation("First");
        context.LogWarning("Second");
        context.LogError("Third");

        // Assert
        var logs = context.GetLogs();
        logs.Should().HaveCount(3);
        logs[0].Message.Should().Be("First");
        logs[1].Message.Should().Be("Second");
        logs[2].Message.Should().Be("Third");
    }

    [Fact]
    public void ReconfigureTimeout_ShouldUpdateCancellationTokenAndConfig()
    {
        // Arrange
        var context = CreateJobContext();
        var newCts = new CancellationTokenSource();
        var newConfig = new JobConsumerConfig { ExecutionTimeoutSeconds = 120 };

        // Act
        context.ReconfigureTimeout(newConfig, newCts.Token);

        // Assert
        context.CancellationToken.Should().Be(newCts.Token);
        context.ExecutorJobConsumerConfig.Should().Be(newConfig);
        context.ExecutorJobConsumerConfig.ExecutionTimeoutSeconds.Should().Be(120);
    }

    [Fact]
    public void Log_ShouldSetTimestampToUtcNow()
    {
        // Arrange
        var context = CreateJobContext();
        var beforeLog = DateTime.UtcNow;

        // Act
        context.LogInformation("Test message");

        // Assert
        var logs = context.GetLogs();
        var afterLog = DateTime.UtcNow;
        logs[0].Timestamp.Should().BeOnOrAfter(beforeLog);
        logs[0].Timestamp.Should().BeOnOrBefore(afterLog);
    }

    [Fact]
    public void LogError_ShouldIncludeInnerExceptionMessage()
    {
        // Arrange
        var context = CreateJobContext();
        var innerException = new ArgumentException("Inner error");
        var outerException = new InvalidOperationException("Outer error", innerException);

        // Act
        context.LogError("Error with inner exception", outerException);

        // Assert
        var logs = context.GetLogs();
        logs.Should().ContainSingle();
        logs[0].Data.Should().ContainKey("InnerException");
        logs[0].Data["InnerException"].Should().Be("Inner error");
    }

    [Fact]
    public void Context_ShouldProvideAccessToScheduledJobProperties()
    {
        // Arrange & Act
        var context = CreateJobContext();

        // Assert
        context.Job.JobNameInWorker.Should().Be("TestJob");
        context.Job.DisplayName.Should().Be("Test Job");
        context.Job.JobData.Should().Be("{\"key\": \"value\"}");
        context.Job.IsActive.Should().BeTrue();
    }

    [Fact]
    public void LogUserFriendlyLogsViaLogger_ShouldNotLogToLogger_WhenDisabled()
    {
        // Arrange
        var config = new JobConsumerConfig { LogUserFriendlyLogsViaLogger = false };
        var context = new JobContext(
            _correlationId,
            _scheduledJob,
            _workerId,
            _loggerMock.Object,
            _outboxServiceMock.Object,
            config,
            CancellationToken.None);

        // Act
        context.LogInformation("Test message");

        // Assert
        _loggerMock.Verify(x => x.Information(It.IsAny<string>(), It.IsAny<object[]>()), Times.Never);
    }

    [Fact]
    public void LogUserFriendlyLogsViaLogger_ShouldLogToLogger_WhenEnabled()
    {
        // Arrange
        var config = new JobConsumerConfig { LogUserFriendlyLogsViaLogger = true };
        var context = new JobContext(
            _correlationId,
            _scheduledJob,
            _workerId,
            _loggerMock.Object,
            _outboxServiceMock.Object,
            config,
            CancellationToken.None);

        // Act
        context.LogInformation("Test message");

        // Assert
        _loggerMock.Verify(x => x.Information(It.IsAny<string>(), It.IsAny<object[]>()), Times.Once);
    }

    private JobContext CreateJobContext() => new(_correlationId, _scheduledJob, _workerId, _loggerMock.Object, _outboxServiceMock.Object, _jobConsumerConfig, CancellationToken.None);
}
