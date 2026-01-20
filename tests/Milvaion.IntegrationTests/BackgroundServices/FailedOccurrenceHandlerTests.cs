using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.Models.Options;
using Milvaion.Infrastructure.BackgroundServices;
using Milvaion.Infrastructure.Services.RabbitMQ;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Models;
using Milvasoft.Milvaion.Sdk.Utils;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.BackgroundServices;

/// <summary>
/// Integration tests for FailedOccurrenceHandler.
/// Tests Dead Letter Queue message processing and failed occurrence storage.
/// </summary>
[Collection(nameof(MilvaionTestCollection))]
public class FailedOccurrenceHandlerTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : BackgroundServiceTestBase(factory, output)
{

    [Fact]
    public async Task ProcessFailedOccurrence_ShouldStoreFailedJobInDatabase()
    {
        // Arrange
        await InitializeAsync();

        var job = await SeedScheduledJobAsync($"FailedTestJob_{Guid.NewGuid():N}");
        var occurrence = await SeedJobOccurrenceAsync(
            jobId: job.Id,
            jobName: job.JobNameInWorker,
            status: JobOccurrenceStatus.Failed
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var uniqueException = $"Test exception message - {Guid.NewGuid():N}";

        // Act - Start the handler first
        var handler = CreateFailedOccurrenceHandler();
        _ = Task.Run(async () =>
        {
            try
            {
                await handler.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Give handler time to start
        await Task.Delay(1000, cts.Token);

        // Publish a message to DLQ
        await PublishToDlqAsync(job, occurrence, uniqueException, cts.Token);

        // Wait for failed occurrence to be stored
        var found = await WaitForConditionAsync(
            async () =>
            {
                var dbContext = GetDbContext();
                var failedOcc = await dbContext.FailedOccurrences
                    .AsNoTracking()
                    .FirstOrDefaultAsync(f => f.OccurrenceId == occurrence.Id, cts.Token);
                return failedOcc != null && failedOcc.Exception?.Contains(uniqueException) == true;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await handler.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("failed occurrence should be stored in database");

        var dbContextAssert = GetDbContext();
        var failedOccurrence = await dbContextAssert.FailedOccurrences
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.OccurrenceId == occurrence.Id, cts.Token);

        failedOccurrence.Should().NotBeNull();
        failedOccurrence!.JobId.Should().Be(job.Id);
        failedOccurrence.OccurrenceId.Should().Be(occurrence.Id);
        failedOccurrence.CorrelationId.Should().Be(occurrence.CorrelationId);
        failedOccurrence.JobNameInWorker.Should().Be(job.JobNameInWorker);
        failedOccurrence.Exception.Should().Contain(uniqueException);
        failedOccurrence.Resolved.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessFailedOccurrence_ShouldTruncateLongException()
    {
        // Arrange
        await InitializeAsync();

        var job = await SeedScheduledJobAsync($"LongExceptionJob_{Guid.NewGuid():N}");
        var occurrence = await SeedJobOccurrenceAsync(
            jobId: job.Id,
            jobName: job.JobNameInWorker,
            status: JobOccurrenceStatus.Failed
        );

        // Create a very long exception (>3KB)
        var longException = new string('X', 5000);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act - Start the handler first
        var handler = CreateFailedOccurrenceHandler();
        _ = Task.Run(async () =>
        {
            try
            {
                await handler.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(1000, cts.Token);

        await PublishToDlqAsync(job, occurrence, longException, cts.Token);

        // Wait for failed occurrence to be stored
        var found = await WaitForConditionAsync(
            async () =>
            {
                var dbContext = GetDbContext();
                var failedOcc = await dbContext.FailedOccurrences
                    .AsNoTracking()
                    .FirstOrDefaultAsync(f => f.OccurrenceId == occurrence.Id, cts.Token);
                return failedOcc != null;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await handler.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("failed occurrence should be stored");

        var dbContextAssert = GetDbContext();
        var failedOccurrence = await dbContextAssert.FailedOccurrences
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.OccurrenceId == occurrence.Id, cts.Token);

        failedOccurrence.Should().NotBeNull();
        // Exception should be truncated to ~3KB
        failedOccurrence!.Exception.Length.Should().BeLessOrEqualTo(3500);
        failedOccurrence.Exception.Should().Contain("truncated");
    }

    [Fact]
    public async Task ProcessFailedOccurrence_ShouldDetermineFailureType_MaxRetriesExceeded()
    {
        // Arrange
        await InitializeAsync();

        var job = await SeedScheduledJobAsync($"MaxRetriesJob_{Guid.NewGuid():N}");
        var occurrence = await SeedJobOccurrenceAsync(
            jobId: job.Id,
            jobName: job.JobNameInWorker,
            status: JobOccurrenceStatus.Failed
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act - Start the handler first
        var handler = CreateFailedOccurrenceHandler();
        _ = Task.Run(async () =>
        {
            try
            {
                await handler.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(1000, cts.Token);

        // Publish with retry count >= max retries
        await PublishToDlqAsync(job, occurrence, "Max retries exceeded", cts.Token, retryCount: 3, maxRetries: 3);

        // Wait for failed occurrence to be stored
        var found = await WaitForConditionAsync(
            async () =>
            {
                var dbContext = GetDbContext();
                var failedOcc = await dbContext.FailedOccurrences
                    .AsNoTracking()
                    .FirstOrDefaultAsync(f => f.OccurrenceId == occurrence.Id, cts.Token);
                return failedOcc != null && failedOcc.FailureType == FailureType.MaxRetriesExceeded;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await handler.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("failed occurrence with MaxRetriesExceeded should be stored");

        var dbContextAssert = GetDbContext();
        var failedOccurrence = await dbContextAssert.FailedOccurrences
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.OccurrenceId == occurrence.Id, cts.Token);

        failedOccurrence.Should().NotBeNull();
        failedOccurrence!.FailureType.Should().Be(FailureType.MaxRetriesExceeded);
        failedOccurrence.RetryCount.Should().Be(3);
    }

    [Fact]
    public async Task ProcessFailedOccurrence_ShouldDetermineFailureType_Timeout()
    {
        // Arrange
        await InitializeAsync();

        var job = await SeedScheduledJobAsync($"TimeoutJob_{Guid.NewGuid():N}");
        var occurrence = await SeedJobOccurrenceAsync(
            jobId: job.Id,
            jobName: job.JobNameInWorker,
            status: JobOccurrenceStatus.TimedOut
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act - Start the handler first
        var handler = CreateFailedOccurrenceHandler();
        _ = Task.Run(async () =>
        {
            try
            {
                await handler.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(1000, cts.Token);

        await PublishToDlqAsync(job, occurrence, "Job timed out", cts.Token, status: JobOccurrenceStatus.TimedOut);

        // Wait for failed occurrence to be stored
        var found = await WaitForConditionAsync(
            async () =>
            {
                var dbContext = GetDbContext();
                var failedOcc = await dbContext.FailedOccurrences
                    .AsNoTracking()
                    .FirstOrDefaultAsync(f => f.OccurrenceId == occurrence.Id, cts.Token);
                return failedOcc != null && failedOcc.FailureType == FailureType.Timeout;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await handler.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("failed occurrence with Timeout should be stored");

        var dbContextAssert = GetDbContext();
        var failedOccurrence = await dbContextAssert.FailedOccurrences
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.OccurrenceId == occurrence.Id, cts.Token);

        failedOccurrence.Should().NotBeNull();
        failedOccurrence!.FailureType.Should().Be(FailureType.Timeout);
    }

    [Fact]
    public async Task ProcessFailedOccurrence_ShouldHandleNullException()
    {
        // Arrange
        await InitializeAsync();

        var job = await SeedScheduledJobAsync($"NullExceptionJob_{Guid.NewGuid():N}");
        var occurrence = await SeedJobOccurrenceAsync(
            jobId: job.Id,
            jobName: job.JobNameInWorker,
            status: JobOccurrenceStatus.Failed
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Act - Start the handler first
        var handler = CreateFailedOccurrenceHandler();
        _ = Task.Run(async () =>
        {
            try
            {
                await handler.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(1000, cts.Token);

        // Publish with null exception
        await PublishToDlqAsync(job, occurrence, null, cts.Token);

        // Wait for failed occurrence to be stored
        var found = await WaitForConditionAsync(
            async () =>
            {
                var dbContext = GetDbContext();
                var failedOcc = await dbContext.FailedOccurrences
                    .AsNoTracking()
                    .FirstOrDefaultAsync(f => f.OccurrenceId == occurrence.Id, cts.Token);
                return failedOcc != null;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await handler.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("failed occurrence should be stored even with null exception");

        var dbContextAssert = GetDbContext();
        var failedOccurrence = await dbContextAssert.FailedOccurrences
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.OccurrenceId == occurrence.Id, cts.Token);

        failedOccurrence.Should().NotBeNull();
        failedOccurrence!.Exception.Should().Contain("Job failed to complete");
    }

    private FailedOccurrenceHandler CreateFailedOccurrenceHandler() => new(
            _serviceProvider,
            _serviceProvider.GetRequiredService<RabbitMQConnectionFactory>(),
            Options.Create(new FailedOccurrenceHandlerOptions
            {
                Enabled = true
            }),
            _serviceProvider.GetRequiredService<ILoggerFactory>()
        );

    private async Task PublishToDlqAsync(
        ScheduledJob job,
        JobOccurrence occurrence,
        string exception,
        CancellationToken cancellationToken,
        int retryCount = 0,
        int maxRetries = 3,
        JobOccurrenceStatus status = JobOccurrenceStatus.Failed)
    {
        var factory = new ConnectionFactory
        {
            HostName = _factory.GetRabbitMqHost(),
            Port = _factory.GetRabbitMqPort(),
            UserName = "guest",
            Password = "guest"
        };

        await using var connection = await factory.CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        // Ensure DLQ exists
        await channel.QueueDeclareAsync(
            queue: WorkerConstant.Queues.FailedOccurrences,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        var message = new DlqJobMessage
        {
            Id = job.Id,
            DisplayName = job.DisplayName,
            JobNameInWorker = job.JobNameInWorker,
            JobData = job.JobData,
            Status = status,
            Exception = exception
        };

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, ConstantJsonOptions.PropNameCaseInsensitive));

        var properties = new BasicProperties
        {
            Persistent = true,
            CorrelationId = occurrence.CorrelationId.ToString(),
            Headers = new Dictionary<string, object>
            {
                ["CorrelationId"] = Encoding.UTF8.GetBytes(occurrence.CorrelationId.ToString()),
                ["x-retry-count"] = retryCount,
                ["MaxRetries"] = maxRetries
            }
        };

        await channel.BasicPublishAsync(
            exchange: "",
            routingKey: WorkerConstant.Queues.FailedOccurrences,
            mandatory: false,
            basicProperties: properties,
            body: body,
            cancellationToken: cancellationToken);
    }
}
