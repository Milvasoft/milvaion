using System.Diagnostics;
using FluentAssertions;
using Hangfire;
using Hangfire.Client;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Domain.JsonModels;
using Milvasoft.Milvaion.Sdk.Worker.Hangfire.Filters;
using Milvasoft.Milvaion.Sdk.Worker.Hangfire.Services;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using Milvasoft.Milvaion.Sdk.Worker.Utils;
using Moq;

namespace Milvaion.UnitTests.HangfireSdkTests;

[Trait("Hangfire SDK Unit Tests", "MilvaionJobFilter unit tests.")]
public class MilvaionJobFilterTests
{
    private readonly Mock<IExternalJobPublisher> _publisherMock;
    private readonly Mock<ILoggerFactory> _loggerFactoryMock;
    private readonly ExternalJobRegistry _jobRegistry;
    private readonly MilvaionJobFilter _filter;

    public MilvaionJobFilterTests()
    {
        _publisherMock = new Mock<IExternalJobPublisher>();
        _loggerFactoryMock = new Mock<ILoggerFactory>();
        _loggerFactoryMock.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

        _jobRegistry = new ExternalJobRegistry();

        var workerOptions = Options.Create(new WorkerOptions
        {
            WorkerId = "hangfire-test-worker",
            ExternalScheduler = new MilvaionExternalSchedulerOptions
            {
                Source = "Hangfire"
            }
        });

        _filter = new MilvaionJobFilter(_publisherMock.Object, workerOptions, _jobRegistry, _loggerFactoryMock.Object);
    }

    #region OnPerforming (IServerFilter)

    [Fact]
    public void OnPerforming_ShouldPublishStartingEvent()
    {
        // Arrange
        _publisherMock.Setup(x => x.PublishOccurrenceEventAsync(It.IsAny<ExternalJobOccurrenceMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var context = CreatePerformingContext();

        // Act
        _filter.OnPerforming(context);

        // Assert - published from a fire-and-forget Task.Run
        VerifyEventually(() => _publisherMock.Verify(x => x.PublishOccurrenceEventAsync(
            It.Is<ExternalJobOccurrenceMessage>(m =>
                m.Status == JobOccurrenceStatus.Running &&
                m.EventType == ExternalOccurrenceEventType.Starting &&
                m.Source == "Hangfire" &&
                m.WorkerId == "hangfire-test-worker"),
            It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact]
    public void OnPerforming_ShouldNotThrow_WhenPublisherIsNull()
    {
        // Arrange
        var workerOptions = Options.Create(new WorkerOptions
        {
            ExternalScheduler = new MilvaionExternalSchedulerOptions { Source = "Hangfire" }
        });

        var filter = new MilvaionJobFilter(null, workerOptions, _jobRegistry, _loggerFactoryMock.Object);
        var context = CreatePerformingContext();

        // Act & Assert
        var act = () => filter.OnPerforming(context);
        act.Should().NotThrow();
    }

    [Fact]
    public void OnPerforming_ShouldNotThrow_WhenPublisherFails()
    {
        // Arrange
        _publisherMock.Setup(x => x.PublishOccurrenceEventAsync(It.IsAny<ExternalJobOccurrenceMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("RabbitMQ failed"));

        var context = CreatePerformingContext();

        // Act & Assert
        var act = () => _filter.OnPerforming(context);
        act.Should().NotThrow();
    }

    #endregion

    #region OnPerformed (IServerFilter)

    [Fact]
    public void OnPerformed_WithoutException_ShouldPublishCompletedStatus()
    {
        // Arrange
        _publisherMock.Setup(x => x.PublishOccurrenceEventAsync(It.IsAny<ExternalJobOccurrenceMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var context = CreatePerformedContext(exception: null);

        // Act
        _filter.OnPerformed(context);

        // Assert - published from a fire-and-forget Task.Run
        VerifyEventually(() => _publisherMock.Verify(x => x.PublishOccurrenceEventAsync(
            It.Is<ExternalJobOccurrenceMessage>(m =>
                m.Status == JobOccurrenceStatus.Completed &&
                m.EventType == ExternalOccurrenceEventType.Completed &&
                m.Exception == null),
            It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact]
    public void OnPerformed_WithException_ShouldPublishFailedStatus()
    {
        // Arrange
        _publisherMock.Setup(x => x.PublishOccurrenceEventAsync(It.IsAny<ExternalJobOccurrenceMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var context = CreatePerformedContext(exception: new InvalidOperationException("Job failed"));

        // Act
        _filter.OnPerformed(context);

        // Assert - published from a fire-and-forget Task.Run
        VerifyEventually(() => _publisherMock.Verify(x => x.PublishOccurrenceEventAsync(
            It.Is<ExternalJobOccurrenceMessage>(m =>
                m.Status == JobOccurrenceStatus.Failed &&
                m.Exception != null),
            It.IsAny<CancellationToken>()), Times.Once));
    }

    #endregion

    #region OnCreating / OnCreated (IClientFilter)

    [Fact]
    public void OnCreating_ShouldRegisterJobInRegistry()
    {
        // Arrange
        var context = CreateCreatingContext();

        // Act
        _filter.OnCreating(context);

        // Assert
        _jobRegistry.Count.Should().Be(1);
    }

    [Fact]
    public void OnCreated_ShouldPublishJobRegistration()
    {
        // Arrange
        _publisherMock.Setup(x => x.PublishJobRegistrationAsync(It.IsAny<ExternalJobRegistrationMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var context = CreateCreatedContext();

        // Act
        _filter.OnCreated(context);

        // Assert - published from a fire-and-forget Task.Run
        VerifyEventually(() => _publisherMock.Verify(x => x.PublishJobRegistrationAsync(
            It.Is<ExternalJobRegistrationMessage>(m =>
                m.Source == "Hangfire" &&
                m.IsActive == true),
            It.IsAny<CancellationToken>()), Times.Once));
    }

    #endregion

    #region OnStateElection (IElectStateFilter)

    [Fact]
    public void OnStateElection_WithDeletedState_ShouldPublishCancelledEvent()
    {
        // Arrange
        _publisherMock.Setup(x => x.PublishOccurrenceEventAsync(It.IsAny<ExternalJobOccurrenceMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var context = CreateElectStateContext(new DeletedState(), Guid.CreateVersion7());

        // Act
        _filter.OnStateElection(context);

        // Assert - published from a fire-and-forget Task.Run
        VerifyEventually(() => _publisherMock.Verify(x => x.PublishOccurrenceEventAsync(
            It.Is<ExternalJobOccurrenceMessage>(m =>
                m.Status == JobOccurrenceStatus.Cancelled &&
                m.EventType == ExternalOccurrenceEventType.Cancelled),
            It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact]
    public void OnStateElection_WithDeletedState_ShouldNotPublish_WhenJobNeverRan()
    {
        // Arrange
        var context = CreateElectStateContext(new DeletedState());

        // Act
        _filter.OnStateElection(context);

        // Allow fire-and-forget Task.Run to potentially fire
        Thread.Sleep(500);

        // Assert
        _publisherMock.Verify(x => x.PublishOccurrenceEventAsync(It.IsAny<ExternalJobOccurrenceMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void OnStateElection_WithNonDeletedState_ShouldNotPublish()
    {
        // Arrange
        var context = CreateElectStateContext(new EnqueuedState());

        // Act
        _filter.OnStateElection(context);

        // Allow fire-and-forget Task.Run to potentially fire
        Thread.Sleep(500);

        // Assert
        _publisherMock.Verify(x => x.PublishOccurrenceEventAsync(It.IsAny<ExternalJobOccurrenceMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    /// <summary>
    /// Retries a Moq verification until it passes. A fixed sleep lost the race on a busy thread pool.
    /// </summary>
    private static void VerifyEventually(Action verify)
    {
        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            try
            {
                verify();
                return;
            }
            catch (MockException) when (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
            {
                Thread.Sleep(25);
            }
        }
    }

    #region Context Factories

    private static Job CreateHangfireJob() => Job.FromExpression(() => DummyJob.Execute());

    private static PerformingContext CreatePerformingContext()
    {
        var job = CreateHangfireJob();
        var backgroundJob = new BackgroundJob("test-job-id", job, DateTime.UtcNow);
        var storage = new Mock<JobStorage>();
        var connection = new Mock<IStorageConnection>();
        var cancellationToken = new Mock<IJobCancellationToken>();

        var performContext = new PerformContext(storage.Object, connection.Object, backgroundJob, cancellationToken.Object);
        return new PerformingContext(performContext);
    }

    private static PerformedContext CreatePerformedContext(Exception exception)
    {
        var job = CreateHangfireJob();
        var backgroundJob = new BackgroundJob("test-job-id", job, DateTime.UtcNow);
        var storage = new Mock<JobStorage>();
        var connection = new Mock<IStorageConnection>();
        var cancellationToken = new Mock<IJobCancellationToken>();

        var performContext = new PerformContext(storage.Object, connection.Object, backgroundJob, cancellationToken.Object);
        return new PerformedContext(performContext, null, false, exception);
    }

    private static CreatingContext CreateCreatingContext()
    {
        var job = CreateHangfireJob();
        var storage = new Mock<JobStorage>();
        var connection = new Mock<IStorageConnection>();

        var createContext = new CreateContext(storage.Object, connection.Object, job, new EnqueuedState());
        return new CreatingContext(createContext);
    }

    private static CreatedContext CreateCreatedContext()
    {
        var job = CreateHangfireJob();
        var backgroundJob = new BackgroundJob("test-job-id", job, DateTime.UtcNow);
        var storage = new Mock<JobStorage>();
        var connection = new Mock<IStorageConnection>();

        var createContext = new CreateContext(storage.Object, connection.Object, job, new EnqueuedState());
        return new CreatedContext(createContext, backgroundJob, false, null);
    }

    private static ElectStateContext CreateElectStateContext(IState candidateState, Guid? correlationId = null)
    {
        var job = CreateHangfireJob();
        var backgroundJob = new BackgroundJob("test-job-id", job, DateTime.UtcNow);
        var storage = new Mock<JobStorage>();
        var connection = new Mock<IStorageConnection>();
        var transaction = new Mock<IWriteOnlyTransaction>();

        if (correlationId.HasValue)
            connection.Setup(x => x.GetJobParameter("test-job-id", "Milvaion_CorrelationId")).Returns($"\"{correlationId}\"");

        var applyContext = new ApplyStateContext(storage.Object, connection.Object, transaction.Object, backgroundJob, candidateState, null);
        return new ElectStateContext(applyContext);
    }

    #endregion

    public static class DummyJob
    {
        public static void Execute() { }
    }
}
