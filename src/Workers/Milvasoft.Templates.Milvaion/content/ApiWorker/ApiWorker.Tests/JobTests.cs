using ApiWorker.Jobs;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Worker.Testing;

namespace ApiWorker.Tests;

/// <summary>
/// Local job tests — no RabbitMQ, Redis, or database required.
///
/// Add a test method for each job you implement in your worker.
/// Use JobTestRunner to run jobs in isolation and assert on the result.
/// </summary>
public class JobTests
{
    private static ILoggerFactory CreateLoggerFactory() => LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));

    [Fact]
    public async Task SimpleJob_ShouldComplete_WhenRunNormally()
    {
        var result = await JobTestRunner.For(new SimpleJob())
                                        .WithLoggerFactory(CreateLoggerFactory())
                                        .RunAsync();

        result.Status.Should().Be(JobOccurrenceStatus.Completed);
        result.Exception.Should().BeNull();
        result.DurationMs.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SimpleJob_ShouldBeCancelled_WhenCancellationRequestedBeforeStart()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await JobTestRunner.For(new SimpleJob())
                                        .WithCancellationToken(cts.Token)
                                        .WithLoggerFactory(CreateLoggerFactory())
                                        .RunAsync();

        result.Status.Should().Be(JobOccurrenceStatus.Cancelled);
    }

    [Fact]
    public async Task SendEmailJob_ShouldComplete_WhenValidDataProvided()
    {
        var jobData = new EmailJobData
        {
            To = "test@example.com",
            Subject = "Hello from local test"
        };

        var result = await JobTestRunner.For(new SendEmailJob())
                                        .WithJobData(jobData)
                                        .WithTimeout(60)
                                        .WithLoggerFactory(CreateLoggerFactory())
                                        .RunAsync();

        result.Status.Should().Be(JobOccurrenceStatus.Completed);
        result.Exception.Should().BeNull();
    }

    [Fact]
    public async Task SendEmailJob_ShouldComplete_WhenNoDataProvided()
    {
        // GetData<EmailJobData>() returns null gracefully — job should handle it
        var result = await JobTestRunner.For(new SendEmailJob())
                                        .WithTimeout(60)
                                        .WithLoggerFactory(CreateLoggerFactory())
                                        .RunAsync();

        result.Status.Should().Be(JobOccurrenceStatus.Completed);
    }
}
