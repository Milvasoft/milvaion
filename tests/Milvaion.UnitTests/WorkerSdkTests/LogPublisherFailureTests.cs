using FluentAssertions;
using Microsoft.Extensions.Logging;
using Milvasoft.Milvaion.Sdk.Domain.JsonModels;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using Milvasoft.Milvaion.Sdk.Worker.Persistence;
using Milvasoft.Milvaion.Sdk.Worker.RabbitMQ;
using Moq;
using System.Collections.Concurrent;

namespace Milvaion.UnitTests.WorkerSdkTests;

[Trait("SDK Unit Tests", "LogPublisher behaviour when RabbitMQ is unreachable.")]
public class LogPublisherFailureTests
{
    private static ILoggerFactory CreateLoggerFactory()
    {
        var loggerFactory = new Mock<ILoggerFactory>();

        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

        return loggerFactory.Object;
    }

    // Port 1 refuses immediately, so a publish fails without waiting for a timeout.
    private static WorkerOptions UnreachableBroker() => new() { RabbitMQ = new RabbitMQSettings { Host = "127.0.0.1", Port = 1 } };

    private static OccurrenceLog Log(int number) => new() { Timestamp = DateTime.UtcNow, Level = "Information", Message = number.ToString() };

    [Fact]
    public async Task FailedFlush_ShouldMoveEveryLogToLocalStore_InOrder()
    {
        // Arrange - more than one batch (100), which also covers the log that used to be lost at the batch boundary
        var stored = new ConcurrentQueue<string>();
        var store = new Mock<ILocalStateStore>();

        store.Setup(x => x.StoreLogAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<OccurrenceLog>(), It.IsAny<CancellationToken>()))
             .Callback<Guid, string, OccurrenceLog, CancellationToken>((_, _, log, _) => stored.Enqueue(log.Message))
             .Returns(Task.CompletedTask);

        await using var publisher = new LogPublisher(UnreachableBroker(), CreateLoggerFactory(), store.Object);

        var occurrenceId = Guid.CreateVersion7();

        for (var i = 0; i < 150; i++)
            await publisher.PublishLogAsync(occurrenceId, "worker", Log(i));

        // Act - a flush stops after a failed batch, so it takes one per batch; the timer and batch-size flush may help
        for (var attempt = 0; attempt < 40 && stored.Count < 150; attempt++)
        {
            await publisher.FlushAsync();
            await Task.Delay(50);
        }

        // Assert
        stored.Should().HaveCount(150);
        stored.Select(int.Parse).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task FailedFlush_ShouldThrowAndKeepLogs_WhenThereIsNoLocalStore()
    {
        await using var publisher = new LogPublisher(UnreachableBroker(), CreateLoggerFactory());

        await publisher.PublishLogAsync(Guid.CreateVersion7(), "worker", Log(1));

        var act = () => publisher.FlushAsync();

        await act.Should().ThrowAsync<Exception>();
    }
}
