using FluentAssertions;
using Microsoft.Extensions.Logging;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Worker.Testing;

namespace SampleWorker.Tests;

/// <summary>
/// Local tests for SampleWorker jobs.
/// No RabbitMQ, Redis, or database required — runs completely offline.
///
/// How to add tests for your own jobs:
/// 1. Copy this file to your worker's test project.
/// 2. Replace SampleWorker job types with your job classes.
/// 3. Use JobTestRunner.For(new YourJob()).RunAsync() to execute the job.
/// 4. Assert on result.Status, result.DurationMs, result.Exception, etc.
/// </summary>
public class SampleJobTests
{
    private static ILoggerFactory CreateLoggerFactory() => LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));

    [Fact]
    public async Task TestJob_ShouldComplete_WhenRunNormally()
    {
        var result = await JobTestRunner
            .For(new TestJob())
            .WithLoggerFactory(CreateLoggerFactory())
            .RunAsync();

        result.Status.Should().Be(JobOccurrenceStatus.Completed);
        result.Exception.Should().BeNull();
        result.DurationMs.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task TestJob_ShouldBeCancelled_WhenCancellationRequestedBeforeStart()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await JobTestRunner
            .For(new TestJob())
            .WithCancellationToken(cts.Token)
            .WithLoggerFactory(CreateLoggerFactory())
            .RunAsync();

        result.Status.Should().Be(JobOccurrenceStatus.Cancelled);
    }

    [Fact]
    public async Task TestJob_ShouldTimeOut_WhenTimeoutIsVeryShort()
    {
        var result = await JobTestRunner
            .For(new TestJob())
            .WithTimeout(1)  // 1 second — job runs 5×500ms = 2.5s
            .WithLoggerFactory(CreateLoggerFactory())
            .RunAsync();

        result.Status.Should().Be(JobOccurrenceStatus.TimedOut);
    }

    [Fact]
    public async Task SampleSendEmailJob_ShouldComplete_WhenJobDataIsNull()
    {
        // Job uses context.GetData<EmailJobData>() which returns null gracefully
        var result = await JobTestRunner
            .For(new SampleSendEmailJob())
            .WithTimeout(1)
            .WithLoggerFactory(CreateLoggerFactory())
            .RunAsync();

        result.Status.Should().Be(JobOccurrenceStatus.Completed);
        result.Exception.Should().BeNull();
    }

    [Fact]
    public async Task AlwaysFailingJob_ShouldFail_WithExceptionMessage()
    {
        var result = await JobTestRunner
            .For(new AlwaysFailingJob())
            .WithLoggerFactory(CreateLoggerFactory())
            .RunAsync();

        result.Status.Should().Be(JobOccurrenceStatus.Failed);
        result.Exception.Should().Contain("always fails");
    }

    [Fact]
    public async Task HaveResultJob_ShouldComplete_AndReturnSerializedResult()
    {
        var result = await JobTestRunner
            .For(new HaveResultJob())
            .WithLoggerFactory(CreateLoggerFactory())
            .RunAsync();

        result.Status.Should().Be(JobOccurrenceStatus.Completed);
        result.Result.Should().NotBeNullOrEmpty();
        result.Result.Should().Contain("Test Product");
    }

    [Fact]
    public async Task LongRunningTestJob_ShouldTimeOut_WhenTimeoutConfigured()
    {
        var result = await JobTestRunner
            .For(new LongRunningTestJob())
            .WithTimeout(1)  // Job runs 20s; timeout at 1s
            .WithLoggerFactory(CreateLoggerFactory())
            .RunAsync();

        result.Status.Should().Be(JobOccurrenceStatus.TimedOut);
        result.Exception.Should().Contain("timeout");
    }
}
