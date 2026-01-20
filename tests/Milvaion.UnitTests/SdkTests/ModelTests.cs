using FluentAssertions;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Domain.JsonModels;
using Milvasoft.Milvaion.Sdk.Models;

namespace Milvaion.UnitTests.SdkTests;

[Trait("SDK Unit Tests", "CachedWorker model unit tests.")]
public class CachedWorkerTests
{
    [Fact]
    public void CachedWorker_ShouldInitializeWithDefaultValues()
    {
        // Act
        var worker = new CachedWorker();

        // Assert
        worker.WorkerId.Should().BeNull();
        worker.DisplayName.Should().BeNull();
        worker.RoutingPatterns.Should().BeEmpty();
        worker.JobDataDefinitions.Should().BeEmpty();
        worker.JobNames.Should().BeEmpty();
        worker.MaxParallelJobs.Should().Be(0);
        worker.Version.Should().BeNull();
        worker.Metadata.Should().BeNull();
        worker.CurrentJobs.Should().Be(0);
        worker.Status.Should().Be(WorkerStatus.Active);
        worker.LastHeartbeat.Should().BeNull();
        worker.Instances.Should().BeEmpty();
    }

    [Fact]
    public void CachedWorker_ShouldSetPropertiesCorrectly()
    {
        // Arrange
        var now = DateTime.UtcNow;

        // Act
        var worker = new CachedWorker
        {
            WorkerId = "email-worker",
            DisplayName = "Email Worker",
            RoutingPatterns = new Dictionary<string, string>
            {
                ["email"] = "jobs.email.*",
                ["notification"] = "jobs.notification.*"
            },
            JobDataDefinitions = new Dictionary<string, string>
            {
                ["email"] = "{}",
            },
            JobNames = ["SendEmailJob", "SendNotificationJob"],
            MaxParallelJobs = 10,
            Version = "1.0.0",
            Metadata = "{\"region\": \"us-east\"}",
            RegisteredAt = now.AddDays(-1),
            CurrentJobs = 3,
            Status = WorkerStatus.Active,
            LastHeartbeat = now.AddSeconds(-10)
        };

        // Assert
        worker.WorkerId.Should().Be("email-worker");
        worker.DisplayName.Should().Be("Email Worker");
        worker.RoutingPatterns.Should().HaveCount(2);
        worker.JobDataDefinitions.Should().HaveCount(1);
        worker.JobNames.Should().HaveCount(2);
        worker.MaxParallelJobs.Should().Be(10);
        worker.Version.Should().Be("1.0.0");
        worker.CurrentJobs.Should().Be(3);
        worker.Status.Should().Be(WorkerStatus.Active);
    }

    [Fact]
    public void CachedWorker_Instances_ShouldStoreMultipleInstances()
    {
        // Arrange
        var now = DateTime.UtcNow;

        // Act
        var worker = new CachedWorker
        {
            WorkerId = "email-worker",
            Instances =
            [
                new WorkerInstance
                {
                    InstanceId = "email-worker-abc123",
                    HostName = "container-1",
                    IpAddress = "10.0.0.1",
                    CurrentJobs = 2,
                    Status = WorkerStatus.Active,
                    LastHeartbeat = now,
                    RegisteredAt = now.AddHours(-1)
                },
                new WorkerInstance
                {
                    InstanceId = "email-worker-def456",
                    HostName = "container-2",
                    IpAddress = "10.0.0.2",
                    CurrentJobs = 1,
                    Status = WorkerStatus.Active,
                    LastHeartbeat = now,
                    RegisteredAt = now.AddHours(-1)
                }
            ]
        };

        // Assert
        worker.Instances.Should().HaveCount(2);
        worker.Instances[0].InstanceId.Should().Be("email-worker-abc123");
        worker.Instances[1].InstanceId.Should().Be("email-worker-def456");
    }
}

[Trait("SDK Unit Tests", "WorkerInstance model unit tests.")]
public class WorkerInstanceTests
{
    [Fact]
    public void WorkerInstance_ShouldInitializeWithDefaultValues()
    {
        // Act
        var instance = new WorkerInstance();

        // Assert
        instance.InstanceId.Should().BeNull();
        instance.HostName.Should().BeNull();
        instance.IpAddress.Should().BeNull();
        instance.CurrentJobs.Should().Be(0);
        instance.Status.Should().Be(WorkerStatus.Active);
        instance.LastHeartbeat.Should().Be(default);
        instance.RegisteredAt.Should().Be(default);
    }

    [Fact]
    public void WorkerInstance_ShouldSetPropertiesCorrectly()
    {
        // Arrange
        var now = DateTime.UtcNow;

        // Act
        var instance = new WorkerInstance
        {
            InstanceId = "worker-01-abc123",
            HostName = "container-abc",
            IpAddress = "192.168.1.100",
            CurrentJobs = 5,
            Status = WorkerStatus.Active,
            LastHeartbeat = now,
            RegisteredAt = now.AddHours(-2)
        };

        // Assert
        instance.InstanceId.Should().Be("worker-01-abc123");
        instance.HostName.Should().Be("container-abc");
        instance.IpAddress.Should().Be("192.168.1.100");
        instance.CurrentJobs.Should().Be(5);
        instance.Status.Should().Be(WorkerStatus.Active);
        instance.LastHeartbeat.Should().Be(now);
    }
}

[Trait("SDK Unit Tests", "DlqJobMessage model unit tests.")]
public class DlqJobMessageTests
{
    [Fact]
    public void DlqJobMessage_ShouldInitializeWithDefaultValues()
    {
        // Act
        var message = new DlqJobMessage();

        // Assert
        message.Id.Should().Be(Guid.Empty);
        message.DisplayName.Should().BeNull();
        message.JobNameInWorker.Should().BeNull();
        message.JobData.Should().BeNull();
        message.ExecuteAt.Should().BeNull();
        message.Status.Should().Be(JobOccurrenceStatus.Queued);
        message.Exception.Should().BeNull();
    }

    [Fact]
    public void DlqJobMessage_ShouldSetPropertiesCorrectly()
    {
        // Arrange
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;

        // Act
        var message = new DlqJobMessage
        {
            Id = id,
            DisplayName = "Failed Email Job",
            JobNameInWorker = "SendEmailJob",
            JobData = "{\"to\": \"test@example.com\"}",
            ExecuteAt = now,
            Status = JobOccurrenceStatus.Failed,
            Exception = "SMTP connection failed"
        };

        // Assert
        message.Id.Should().Be(id);
        message.DisplayName.Should().Be("Failed Email Job");
        message.JobNameInWorker.Should().Be("SendEmailJob");
        message.JobData.Should().Be("{\"to\": \"test@example.com\"}");
        message.ExecuteAt.Should().Be(now);
        message.Status.Should().Be(JobOccurrenceStatus.Failed);
        message.Exception.Should().Be("SMTP connection failed");
    }
}

[Trait("SDK Unit Tests", "JobExecutionResult model unit tests.")]
public class JobExecutionResultTests
{
    [Fact]
    public void JobExecutionResult_ShouldInitializeWithDefaultValues()
    {
        // Act
        var result = new JobExecutionResult();

        // Assert
        result.CorrelationId.Should().Be(Guid.Empty);
        result.JobId.Should().Be(Guid.Empty);
        result.WorkerId.Should().BeNull();
        result.Status.Should().Be(JobOccurrenceStatus.Queued);
        result.StartTime.Should().Be(default);
        result.EndTime.Should().Be(default);
        result.DurationMs.Should().Be(0);
        result.Result.Should().BeNull();
        result.Exception.Should().BeNull();
        result.Logs.Should().BeNull();
    }

    [Fact]
    public void JobExecutionResult_ShouldSetPropertiesCorrectly()
    {
        // Arrange
        var correlationId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var logs = new List<OccurrenceLog>
        {
            new() { Timestamp = now, Level = "Information", Message = "Job started" },
            new() { Timestamp = now.AddSeconds(5), Level = "Information", Message = "Job completed" }
        };

        // Act
        var result = new JobExecutionResult
        {
            CorrelationId = correlationId,
            JobId = jobId,
            WorkerId = "worker-01",
            Status = JobOccurrenceStatus.Completed,
            StartTime = now.AddSeconds(-10),
            EndTime = now,
            DurationMs = 10000,
            Result = "Email sent successfully",
            Exception = null,
            Logs = logs
        };

        // Assert
        result.CorrelationId.Should().Be(correlationId);
        result.JobId.Should().Be(jobId);
        result.WorkerId.Should().Be("worker-01");
        result.Status.Should().Be(JobOccurrenceStatus.Completed);
        result.DurationMs.Should().Be(10000);
        result.Result.Should().Be("Email sent successfully");
        result.Logs.Should().HaveCount(2);
    }

    [Fact]
    public void JobExecutionResult_FailedResult_ShouldHaveException()
    {
        // Act
        var result = new JobExecutionResult
        {
            Status = JobOccurrenceStatus.Failed,
            Exception = "Type: InvalidOperationException\nMessage: Operation failed\nStackTrace: ..."
        };

        // Assert
        result.Status.Should().Be(JobOccurrenceStatus.Failed);
        result.Exception.Should().NotBeNullOrEmpty();
        result.Exception.Should().Contain("InvalidOperationException");
    }
}
