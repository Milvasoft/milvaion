using FluentAssertions;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Domain.JsonModels;
using Milvasoft.Milvaion.Sdk.Models;

namespace Milvaion.UnitTests.SdkTests;

[Trait("SDK Unit Tests", "WorkerDiscoveryRequest model unit tests.")]
public class WorkerDiscoveryRequestTests
{
    [Fact]
    public void WorkerDiscoveryRequest_ShouldInitializeWithDefaultValues()
    {
        // Act
        var request = new WorkerDiscoveryRequest();

        // Assert
        request.WorkerId.Should().BeNull();
        request.InstanceId.Should().BeNull();
        request.DisplayName.Should().BeNull();
        request.HostName.Should().BeNull();
        request.IpAddress.Should().BeNull();
        request.RoutingPatterns.Should().BeEmpty();
        request.JobTypes.Should().BeEmpty();
        request.MaxParallelJobs.Should().Be(0);
        request.Version.Should().BeNull();
        request.Metadata.Should().BeNull();
    }

    [Fact]
    public void WorkerDiscoveryRequest_ShouldSetPropertiesCorrectly()
    {
        // Act
        var request = new WorkerDiscoveryRequest
        {
            WorkerId = "email-worker",
            InstanceId = "email-worker-abc123",
            DisplayName = "Email Processing Worker",
            HostName = "container-email-01",
            IpAddress = "10.0.0.50",
            RoutingPatterns = new Dictionary<string, string>
            {
                ["email"] = "jobs.email.*"
            },
            JobTypes = ["SendEmailJob", "SendBulkEmailJob"],
            MaxParallelJobs = 20,
            Version = "2.1.0",
            Metadata = "{\"environment\": \"production\"}"
        };

        // Assert
        request.WorkerId.Should().Be("email-worker");
        request.InstanceId.Should().Be("email-worker-abc123");
        request.DisplayName.Should().Be("Email Processing Worker");
        request.HostName.Should().Be("container-email-01");
        request.IpAddress.Should().Be("10.0.0.50");
        request.RoutingPatterns.Should().HaveCount(1);
        request.JobTypes.Should().HaveCount(2);
        request.MaxParallelJobs.Should().Be(20);
        request.Version.Should().Be("2.1.0");
        request.Metadata.Should().Contain("production");
    }
}

[Trait("SDK Unit Tests", "WorkerHeartbeatMessage model unit tests.")]
public class WorkerHeartbeatMessageTests
{
    [Fact]
    public void WorkerHeartbeatMessage_ShouldInitializeWithDefaultValues()
    {
        // Act
        var message = new WorkerHeartbeatMessage();

        // Assert
        message.WorkerId.Should().BeNull();
        message.InstanceId.Should().BeNull();
        message.CurrentJobs.Should().Be(0);
        message.Timestamp.Should().Be(default);
    }

    [Fact]
    public void WorkerHeartbeatMessage_ShouldSetPropertiesCorrectly()
    {
        // Arrange
        var now = DateTime.UtcNow;

        // Act
        var message = new WorkerHeartbeatMessage
        {
            WorkerId = "email-worker",
            InstanceId = "email-worker-abc123",
            CurrentJobs = 5,
            Timestamp = now
        };

        // Assert
        message.WorkerId.Should().Be("email-worker");
        message.InstanceId.Should().Be("email-worker-abc123");
        message.CurrentJobs.Should().Be(5);
        message.Timestamp.Should().Be(now);
    }
}

[Trait("SDK Unit Tests", "JobStatusUpdateMessage model unit tests.")]
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
    public void JobStatusUpdateMessage_Running_ShouldSetPropertiesCorrectly()
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
            WorkerId = "worker-01",
            Status = JobOccurrenceStatus.Running,
            StartTime = now,
            MessageTimestamp = now
        };

        // Assert
        message.CorrelationId.Should().Be(correlationId);
        message.JobId.Should().Be(jobId);
        message.WorkerId.Should().Be("worker-01");
        message.Status.Should().Be(JobOccurrenceStatus.Running);
        message.StartTime.Should().Be(now);
    }

    [Fact]
    public void JobStatusUpdateMessage_Completed_ShouldSetPropertiesCorrectly()
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
            WorkerId = "worker-01",
            Status = JobOccurrenceStatus.Completed,
            StartTime = now.AddMinutes(-5),
            EndTime = now,
            DurationMs = 300000,
            Result = "Job completed successfully"
        };

        // Assert
        message.Status.Should().Be(JobOccurrenceStatus.Completed);
        message.EndTime.Should().Be(now);
        message.DurationMs.Should().Be(300000);
        message.Result.Should().Be("Job completed successfully");
        message.Exception.Should().BeNull();
    }

    [Fact]
    public void JobStatusUpdateMessage_Failed_ShouldIncludeException()
    {
        // Act
        var message = new JobStatusUpdateMessage
        {
            Status = JobOccurrenceStatus.Failed,
            EndTime = DateTime.UtcNow,
            Exception = "Type: InvalidOperationException\nMessage: Database connection failed"
        };

        // Assert
        message.Status.Should().Be(JobOccurrenceStatus.Failed);
        message.Exception.Should().NotBeNullOrEmpty();
        message.Exception.Should().Contain("Database connection failed");
    }
}

[Trait("SDK Unit Tests", "WorkerLogMessage model unit tests.")]
public class WorkerLogMessageTests
{
    [Fact]
    public void WorkerLogMessage_ShouldInitializeWithDefaults()
    {
        // Act
        var message = new WorkerLogMessage();

        // Assert
        message.CorrelationId.Should().Be(Guid.Empty);
        message.WorkerId.Should().BeNull();
        message.Log.Should().BeNull();
        message.MessageTimestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void WorkerLogMessage_ShouldSetPropertiesCorrectly()
    {
        // Arrange
        var correlationId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var log = new OccurrenceLog
        {
            Timestamp = now,
            Level = "Information",
            Message = "Processing order #12345",
            Category = "OrderProcessing"
        };

        // Act
        var message = new WorkerLogMessage
        {
            CorrelationId = correlationId,
            WorkerId = "order-worker-01",
            Log = log,
            MessageTimestamp = now
        };

        // Assert
        message.CorrelationId.Should().Be(correlationId);
        message.WorkerId.Should().Be("order-worker-01");
        message.Log.Should().NotBeNull();
        message.Log.Message.Should().Be("Processing order #12345");
        message.MessageTimestamp.Should().Be(now);
    }
}
