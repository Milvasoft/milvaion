using FluentAssertions;
using Microsoft.Extensions.Logging;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Milvasoft.Milvaion.Sdk.Worker.Core;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using Moq;

namespace Milvaion.UnitTests.WorkerSdkTests;

[Trait("SDK Unit Tests", "JobExecutor unit tests.")]
public class JobExecutorTests
{
    private readonly Mock<ILoggerFactory> _loggerFactoryMock;
    private readonly Mock<IMilvaLogger> _loggerMock;
    private readonly JobExecutor _jobExecutor;

    public JobExecutorTests()
    {
        _loggerFactoryMock = new Mock<ILoggerFactory>();
        _loggerMock = new Mock<IMilvaLogger>();

        _loggerFactoryMock.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

        _jobExecutor = new JobExecutor(_loggerFactoryMock.Object);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnCompleted_WhenAsyncJobExecutesSuccessfully()
    {
        // Arrange
        var correlationId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var workerId = "test-worker";
        var scheduledJob = CreateScheduledJob(jobId, "TestAsyncJob");

        var mockJob = new Mock<IAsyncJob>();
        mockJob.Setup(x => x.ExecuteAsync(It.IsAny<IJobContext>())).Returns(Task.CompletedTask);

        var jobConsumerConfig = new JobConsumerConfig { ExecutionTimeoutSeconds = 60 };
        var workerOptions = new WorkerOptions { WorkerId = workerId };
        workerOptions.RegenerateInstanceId();

        // Act
        var result = await _jobExecutor.ExecuteAsync(
            mockJob.Object,
            scheduledJob,
            correlationId,
            null,
            workerOptions,
            jobConsumerConfig,
            CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be(JobOccurrenceStatus.Completed);
        result.CorrelationId.Should().Be(correlationId);
        result.JobId.Should().Be(jobId);
        result.WorkerId.Should().Contain(workerId);
        result.DurationMs.Should().BeGreaterOrEqualTo(0);
        result.Exception.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnCompleted_WhenAsyncJobWithResultReturnsValue()
    {
        // Arrange
        var correlationId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var workerId = "test-worker";
        var scheduledJob = CreateScheduledJob(jobId, "TestAsyncJobWithResult");
        var expectedResult = "Job completed successfully with result";

        var mockJob = new Mock<IAsyncJobWithResult>();
        mockJob.Setup(x => x.ExecuteAsync(It.IsAny<IJobContext>())).ReturnsAsync(expectedResult);

        var jobConsumerConfig = new JobConsumerConfig { ExecutionTimeoutSeconds = 60 };
        var workerOptions = new WorkerOptions { WorkerId = workerId };
        workerOptions.RegenerateInstanceId();

        // Act
        var result = await _jobExecutor.ExecuteAsync(
            mockJob.Object,
            scheduledJob,
            correlationId,
            null,
            workerOptions,
            jobConsumerConfig,
            CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be(JobOccurrenceStatus.Completed);
        result.Result.Should().Be(expectedResult);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnCompleted_WhenSyncJobExecutesSuccessfully()
    {
        // Arrange
        var correlationId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var workerId = "test-worker";
        var scheduledJob = CreateScheduledJob(jobId, "TestSyncJob");

        var mockJob = new Mock<IJob>();
        mockJob.Setup(x => x.Execute(It.IsAny<IJobContext>()));

        var jobConsumerConfig = new JobConsumerConfig { ExecutionTimeoutSeconds = 60 };
        var workerOptions = new WorkerOptions { WorkerId = workerId };
        workerOptions.RegenerateInstanceId();

        // Act
        var result = await _jobExecutor.ExecuteAsync(
            mockJob.Object,
            scheduledJob,
            correlationId,
            null,
            workerOptions,
            jobConsumerConfig,
            CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be(JobOccurrenceStatus.Completed);
        result.Exception.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnCompleted_WhenSyncJobWithResultReturnsValue()
    {
        // Arrange
        var correlationId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var workerId = "test-worker";
        var scheduledJob = CreateScheduledJob(jobId, "TestSyncJobWithResult");
        var expectedResult = "Sync job result";

        var mockJob = new Mock<IJobWithResult>();
        mockJob.Setup(x => x.Execute(It.IsAny<IJobContext>())).Returns(expectedResult);

        var jobConsumerConfig = new JobConsumerConfig { ExecutionTimeoutSeconds = 60 };
        var workerOptions = new WorkerOptions { WorkerId = workerId };
        workerOptions.RegenerateInstanceId();

        // Act
        var result = await _jobExecutor.ExecuteAsync(
            mockJob.Object,
            scheduledJob,
            correlationId,
            null,
            workerOptions,
            jobConsumerConfig,
            CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be(JobOccurrenceStatus.Completed);
        result.Result.Should().Be(expectedResult);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnFailed_WhenJobThrowsException()
    {
        // Arrange
        var correlationId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var workerId = "test-worker";
        var scheduledJob = CreateScheduledJob(jobId, "TestFailingJob");
        var expectedException = new InvalidOperationException("Test exception message");

        var mockJob = new Mock<IAsyncJob>();
        mockJob.Setup(x => x.ExecuteAsync(It.IsAny<IJobContext>())).ThrowsAsync(expectedException);

        var jobConsumerConfig = new JobConsumerConfig { ExecutionTimeoutSeconds = 60 };
        var workerOptions = new WorkerOptions { WorkerId = workerId };
        workerOptions.RegenerateInstanceId();

        // Act
        var result = await _jobExecutor.ExecuteAsync(
            mockJob.Object,
            scheduledJob,
            correlationId,
            null,
            workerOptions,
            jobConsumerConfig,
            CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be(JobOccurrenceStatus.Failed);
        result.Exception.Should().Contain("InvalidOperationException");
        result.Exception.Should().Contain("Test exception message");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnCancelled_WhenCancellationIsRequested()
    {
        // Arrange
        var correlationId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var workerId = "test-worker";
        var scheduledJob = CreateScheduledJob(jobId, "TestCancellableJob");

        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var mockJob = new Mock<IAsyncJob>();
        mockJob.Setup(x => x.ExecuteAsync(It.IsAny<IJobContext>()))
               .ThrowsAsync(new OperationCanceledException());

        var jobConsumerConfig = new JobConsumerConfig { ExecutionTimeoutSeconds = 60 };
        var workerOptions = new WorkerOptions { WorkerId = workerId };
        workerOptions.RegenerateInstanceId();

        // Act
        var result = await _jobExecutor.ExecuteAsync(
            mockJob.Object,
            scheduledJob,
            correlationId,
            null,
            workerOptions,
            jobConsumerConfig,
            cts.Token);

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be(JobOccurrenceStatus.Cancelled);
        result.Result.Should().Contain("cancelled");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnTimedOut_WhenJobExceedsTimeout()
    {
        // Arrange
        var correlationId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var workerId = "test-worker";
        var scheduledJob = CreateScheduledJob(jobId, "TestTimeoutJob");

        var mockJob = new Mock<IAsyncJob>();
        mockJob.Setup(x => x.ExecuteAsync(It.IsAny<IJobContext>()))
               .Returns<IJobContext>(async ctx =>
               {
                   // Use the context's cancellation token so it can be cancelled by timeout
                   await Task.Delay(5000, ctx.CancellationToken);
               });

        var jobConsumerConfig = new JobConsumerConfig { ExecutionTimeoutSeconds = 1 }; // 1 second timeout
        var workerOptions = new WorkerOptions { WorkerId = workerId };
        workerOptions.RegenerateInstanceId();

        // Act
        var result = await _jobExecutor.ExecuteAsync(
            mockJob.Object,
            scheduledJob,
            correlationId,
            null,
            workerOptions,
            jobConsumerConfig,
            CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be(JobOccurrenceStatus.TimedOut);
        result.Exception.Should().Contain("timeout");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPopulateLogs_WhenJobLogsMessages()
    {
        // Arrange
        var correlationId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var workerId = "test-worker";
        var scheduledJob = CreateScheduledJob(jobId, "TestLoggingJob");

        var mockJob = new Mock<IAsyncJob>();
        mockJob.Setup(x => x.ExecuteAsync(It.IsAny<IJobContext>()))
               .Callback<IJobContext>(ctx =>
               {
                   ctx.LogInformation("Test log message");
                   ctx.LogWarning("Test warning");
               })
               .Returns(Task.CompletedTask);

        var jobConsumerConfig = new JobConsumerConfig { ExecutionTimeoutSeconds = 60 };
        var workerOptions = new WorkerOptions { WorkerId = workerId };
        workerOptions.RegenerateInstanceId();

        // Act
        var result = await _jobExecutor.ExecuteAsync(
            mockJob.Object,
            scheduledJob,
            correlationId,
            null,
            workerOptions,
            jobConsumerConfig,
            CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be(JobOccurrenceStatus.Completed);
        result.Logs.Should().NotBeEmpty();
        result.Logs.Should().Contain(l => l.Message == "Test log message");
        result.Logs.Should().Contain(l => l.Message == "Test warning");
    }

    [Fact]
    public async Task ExecuteAsync_ShouldTrackDuration_Accurately()
    {
        // Arrange
        var correlationId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var workerId = "test-worker";
        var scheduledJob = CreateScheduledJob(jobId, "TestDurationJob");
        var delayMs = 100;

        var mockJob = new Mock<IAsyncJob>();
        mockJob.Setup(x => x.ExecuteAsync(It.IsAny<IJobContext>()))
               .Returns(Task.Delay(delayMs));

        var jobConsumerConfig = new JobConsumerConfig { ExecutionTimeoutSeconds = 60 };
        var workerOptions = new WorkerOptions { WorkerId = workerId };
        workerOptions.RegenerateInstanceId();

        // Act
        var result = await _jobExecutor.ExecuteAsync(
            mockJob.Object,
            scheduledJob,
            correlationId,
            null,
            workerOptions,
            jobConsumerConfig,
            CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.DurationMs.Should().BeGreaterOrEqualTo(delayMs - 20); // Allow some tolerance
        result.StartTime.Should().BeBefore(result.EndTime);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldUseNoTimeout_WhenTimeoutIsZero()
    {
        // Arrange
        var correlationId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var workerId = "test-worker";
        var scheduledJob = CreateScheduledJob(jobId, "TestNoTimeoutJob");

        var mockJob = new Mock<IAsyncJob>();
        mockJob.Setup(x => x.ExecuteAsync(It.IsAny<IJobContext>())).Returns(Task.CompletedTask);

        var jobConsumerConfig = new JobConsumerConfig { ExecutionTimeoutSeconds = 0 }; // No timeout
        var workerOptions = new WorkerOptions { WorkerId = workerId };
        workerOptions.RegenerateInstanceId();

        // Act
        var result = await _jobExecutor.ExecuteAsync(
            mockJob.Object,
            scheduledJob,
            correlationId,
            null,
            workerOptions,
            jobConsumerConfig,
            CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be(JobOccurrenceStatus.Completed);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnDefaultResult_WhenAsyncJobWithResultReturnsNull()
    {
        // Arrange
        var correlationId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var workerId = "test-worker";
        var scheduledJob = CreateScheduledJob(jobId, "TestNullResultJob");

        var mockJob = new Mock<IAsyncJobWithResult>();
        mockJob.Setup(x => x.ExecuteAsync(It.IsAny<IJobContext>())).ReturnsAsync((string)null);

        var jobConsumerConfig = new JobConsumerConfig { ExecutionTimeoutSeconds = 60 };
        var workerOptions = new WorkerOptions { WorkerId = workerId };
        workerOptions.RegenerateInstanceId();

        // Act
        var result = await _jobExecutor.ExecuteAsync(
            mockJob.Object,
            scheduledJob,
            correlationId,
            null,
            workerOptions,
            jobConsumerConfig,
            CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be(JobOccurrenceStatus.Completed);
        result.Result.Should().Contain("completed successfully"); // Default message
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFormatExceptionWithInnerException_WhenJobThrowsNestedExceptions()
    {
        // Arrange
        var correlationId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var workerId = "test-worker";
        var scheduledJob = CreateScheduledJob(jobId, "TestNestedExceptionJob");
        var innerException = new ArgumentException("Inner error");
        var outerException = new InvalidOperationException("Outer error", innerException);

        var mockJob = new Mock<IAsyncJob>();
        mockJob.Setup(x => x.ExecuteAsync(It.IsAny<IJobContext>())).ThrowsAsync(outerException);

        var jobConsumerConfig = new JobConsumerConfig { ExecutionTimeoutSeconds = 60 };
        var workerOptions = new WorkerOptions { WorkerId = workerId };
        workerOptions.RegenerateInstanceId();

        // Act
        var result = await _jobExecutor.ExecuteAsync(
            mockJob.Object,
            scheduledJob,
            correlationId,
            null,
            workerOptions,
            jobConsumerConfig,
            CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be(JobOccurrenceStatus.Failed);
        result.Exception.Should().Contain("Outer error");
        result.Exception.Should().Contain("Inner error");
        result.Exception.Should().Contain("Inner Exception");
    }

    private static ScheduledJob CreateScheduledJob(Guid jobId, string jobNameInWorker) => new()
    {
        Id = jobId,
        JobNameInWorker = jobNameInWorker,
        DisplayName = $"Test {jobNameInWorker}",
        Description = "Test job for unit testing",
        IsActive = true,
        ExecuteAt = DateTime.UtcNow,
        JobData = "{}"
    };
}
