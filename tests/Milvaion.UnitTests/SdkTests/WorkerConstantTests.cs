using FluentAssertions;
using Milvasoft.Milvaion.Sdk.Utils;

namespace Milvaion.UnitTests.SdkTests;

[Trait("SDK Unit Tests", "WorkerConstant unit tests.")]
public class WorkerConstantTests
{
    [Fact]
    public void DeadLetterRoutingKey_ShouldHaveCorrectValue()
        // Assert
        => WorkerConstant.DeadLetterRoutingKey.Should().Be("failed_jobs");

    [Fact]
    public void DeadLetterExchangeName_ShouldHaveCorrectValue()
        // Assert
        => WorkerConstant.DeadLetterExchangeName.Should().Be("dlx_scheduled_jobs");

    [Fact]
    public void ExchangeName_ShouldHaveCorrectValue()
        // Assert
        => WorkerConstant.ExchangeName.Should().Be("jobs.topic");

    [Fact]
    public void Queues_Jobs_ShouldHaveCorrectValue()
        // Assert
        => WorkerConstant.Queues.Jobs.Should().Be("scheduled_jobs_queue");

    [Fact]
    public void Queues_WorkerLogs_ShouldHaveCorrectValue()
        // Assert
        => WorkerConstant.Queues.WorkerLogs.Should().Be("worker_logs_queue");

    [Fact]
    public void Queues_WorkerHeartbeat_ShouldHaveCorrectValue()
        // Assert
        => WorkerConstant.Queues.WorkerHeartbeat.Should().Be("worker_heartbeat_queue");

    [Fact]
    public void Queues_WorkerRegistration_ShouldHaveCorrectValue()
        // Assert
        => WorkerConstant.Queues.WorkerRegistration.Should().Be("worker_registration_queue");

    [Fact]
    public void Queues_StatusUpdates_ShouldHaveCorrectValue()
        // Assert
        => WorkerConstant.Queues.StatusUpdates.Should().Be("job_status_updates_queue");

    [Fact]
    public void Queues_FailedOccurrences_ShouldHaveCorrectValue()
        // Assert
        => WorkerConstant.Queues.FailedOccurrences.Should().Be("failed_jobs_queue");
}
