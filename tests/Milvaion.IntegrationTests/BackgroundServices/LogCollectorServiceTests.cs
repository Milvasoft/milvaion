using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.Models.Options;
using Milvaion.Infrastructure.BackgroundServices;
using Milvaion.Infrastructure.Services.RabbitMQ;
using Milvaion.Infrastructure.Telemetry;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Domain.JsonModels;
using Milvasoft.Milvaion.Sdk.Utils;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.BackgroundServices;

/// <summary>
/// Integration tests for LogCollectorService.
/// Tests log message consumption from RabbitMQ and batch processing.
/// </summary>
[Collection(nameof(ServicesTestCollection))]
public class LogCollectorServiceTests(ServicesWebApplicationFactory factory, ITestOutputHelper output) : BackgroundServiceTestBase(factory, output)
{
    [Fact]
    public async Task CollectLogs_ShouldAppendLogsToOccurrence()
    {
        // Arrange
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var job = await SeedScheduledJobAsync("LogTestJob");
        var occurrence = await SeedJobOccurrenceAsync(
            jobId: job.Id,
            jobName: job.JobNameInWorker,
            status: JobOccurrenceStatus.Running
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Unique message to avoid cross-test interference
        var uniqueMessage = $"Test log message from worker {Guid.CreateVersion7():N}";

        var logMessage = new WorkerLogBatchMessage
        {
            Logs =
            [
                new() {
                    OccurrenceId = occurrence.CorrelationId,
                    WorkerId = "test-worker",
                    Log = new OccurrenceLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Information",
                        Message = uniqueMessage,
                        Category = "TestCategory"
                    },
                    MessageTimestamp = DateTime.UtcNow
                }
            ],
            BatchTimestamp = DateTime.UtcNow
        };

        // Act - Start the collector first
        var collector = CreateLogCollectorService();
        _ = Task.Run(async () =>
        {
            try
            {
                await collector.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        // Give collector time to start
        await Task.Delay(3000, cts.Token);

        // Publish log message
        await PublishLogMessageAsync(logMessage, cts.Token);

        // Wait for condition with polling
        var found = await WaitForConditionAsync(
            async () =>
            {
                var occ = await GetOccurrenceAsync(occurrence.Id, cts.Token);
                return occ?.Logs?.Any(l => l.Message == uniqueMessage) == true;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await collector.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("log message should be appended to occurrence");

        var updatedOccurrence = await GetOccurrenceAsync(occurrence.Id, cts.Token);
        var workerLog = updatedOccurrence.Logs.FirstOrDefault(l => l.Message == uniqueMessage);
        workerLog.Should().NotBeNull();
        workerLog!.Level.Should().Be("Information");
        workerLog.Category.Should().Be("TestCategory");
    }

    [Fact]
    public async Task CollectLogs_ShouldBatchProcessMultipleLogs()
    {
        // Arrange
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var job = await SeedScheduledJobAsync("BatchLogJob");
        var occurrence = await SeedJobOccurrenceAsync(
            jobId: job.Id,
            jobName: job.JobNameInWorker,
            status: JobOccurrenceStatus.Running
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Unique category to avoid cross-test interference
        var uniqueCategory = $"BatchTest_{Guid.CreateVersion7():N}";

        // Act - Start the collector first
        var collector = CreateLogCollectorService();
        _ = Task.Run(async () =>
        {
            try
            {
                await collector.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(3000, cts.Token);

        var logBatchMessage = new WorkerLogBatchMessage
        {
            Logs = [],
            BatchTimestamp = DateTime.UtcNow
        };

        // Publish multiple log messages
        for (int i = 0; i < 5; i++)
        {
            var logMessage = new WorkerLogMessage
            {
                OccurrenceId = occurrence.CorrelationId,
                WorkerId = "test-worker",
                Log = new OccurrenceLog
                {
                    Timestamp = DateTime.UtcNow,
                    Level = "Information",
                    Message = $"Batch log message {i}",
                    Category = uniqueCategory
                },
                MessageTimestamp = DateTime.UtcNow
            };

            logBatchMessage.Logs.Add(logMessage);
        }

        await PublishLogMessageAsync(logBatchMessage, cts.Token);

        // Wait for all 5 logs
        var found = await WaitForConditionAsync(
            async () =>
            {
                var occ = await GetOccurrenceAsync(occurrence.Id, cts.Token);
                return occ?.Logs?.Count(l => l.Category == uniqueCategory) >= 5;
            },
            timeout: TimeSpan.FromSeconds(20),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await collector.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("all 5 batch logs should be processed");

        var updatedOccurrence = await GetOccurrenceAsync(occurrence.Id, cts.Token);
        var batchLogs = updatedOccurrence.Logs.Where(l => l.Category == uniqueCategory).ToList();
        batchLogs.Should().HaveCount(5);

        for (int i = 0; i < 5; i++)
        {
            batchLogs.Should().Contain(l => l.Message == $"Batch log message {i}");
        }
    }

    [Fact]
    public async Task CollectLogs_ShouldHandleMultipleOccurrences()
    {
        // Arrange
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var job1 = await SeedScheduledJobAsync("LogJob1");
        var job2 = await SeedScheduledJobAsync("LogJob2");

        var occurrence1 = await SeedJobOccurrenceAsync(
            jobId: job1.Id,
            jobName: job1.JobNameInWorker,
            status: JobOccurrenceStatus.Running
        );

        var occurrence2 = await SeedJobOccurrenceAsync(
            jobId: job2.Id,
            jobName: job2.JobNameInWorker,
            status: JobOccurrenceStatus.Running
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var uniqueCategory = $"MultiTest_{Guid.CreateVersion7():N}";
        var message1 = $"Log for occurrence 1 - {Guid.CreateVersion7():N}";
        var message2 = $"Log for occurrence 2 - {Guid.CreateVersion7():N}";

        // Act - Start the collector first
        var collector = CreateLogCollectorService();
        _ = Task.Run(async () =>
        {
            try
            {
                await collector.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(3000, cts.Token);

        // Publish logs for both occurrences
        await PublishLogMessageAsync(new WorkerLogBatchMessage
        {
            BatchTimestamp = DateTime.UtcNow,
            Logs = [
                new WorkerLogMessage
                {
                    OccurrenceId = occurrence1.CorrelationId,
                    WorkerId = "worker-1",
                    Log = new OccurrenceLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Information",
                        Message = message1,
                        Category = uniqueCategory
                    }
                }
            ]
        }, cts.Token);

        await PublishLogMessageAsync(new WorkerLogBatchMessage
        {
            BatchTimestamp = DateTime.UtcNow,
            Logs = [
                new WorkerLogMessage
                {
                    OccurrenceId = occurrence2.CorrelationId,
                    WorkerId = "worker-2",
                    Log = new OccurrenceLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Warning",
                        Message = message2,
                        Category = uniqueCategory
                    }
                }
            ]
        }, cts.Token);

        // Wait for both logs
        var found = await WaitForConditionAsync(
            async () =>
            {
                var occ1 = await GetOccurrenceAsync(occurrence1.Id, cts.Token);
                var occ2 = await GetOccurrenceAsync(occurrence2.Id, cts.Token);
                return occ1?.Logs?.Any(l => l.Message == message1) == true &&
                       occ2?.Logs?.Any(l => l.Message == message2) == true;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await collector.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("both logs should be processed");

        var updatedOccurrence1 = await GetOccurrenceAsync(occurrence1.Id, cts.Token);
        var updatedOccurrence2 = await GetOccurrenceAsync(occurrence2.Id, cts.Token);

        var log1 = updatedOccurrence1.Logs.FirstOrDefault(l => l.Message == message1);
        var log2 = updatedOccurrence2.Logs.FirstOrDefault(l => l.Message == message2);

        log1.Should().NotBeNull();
        log1!.Level.Should().Be("Information");

        log2.Should().NotBeNull();
        log2!.Level.Should().Be("Warning");
    }

    [Fact]
    public async Task CollectLogs_ShouldHandleDifferentLogLevels()
    {
        // Arrange
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var job = await SeedScheduledJobAsync("LogLevelJob");
        var occurrence = await SeedJobOccurrenceAsync(
            jobId: job.Id,
            jobName: job.JobNameInWorker,
            status: JobOccurrenceStatus.Running
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var uniqueCategory = $"LevelTest_{Guid.CreateVersion7():N}";
        var logLevels = new[] { "Debug", "Information", "Warning", "Error", "Critical" };

        // Act - Start the collector first
        var collector = CreateLogCollectorService();
        _ = Task.Run(async () =>
        {
            try
            {
                await collector.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(3000, cts.Token);

        foreach (var level in logLevels)
        {
            await PublishLogMessageAsync(new WorkerLogBatchMessage
            {
                BatchTimestamp = DateTime.UtcNow,
                Logs = [
                    new WorkerLogMessage
                    {
                        OccurrenceId = occurrence.CorrelationId,
                        WorkerId = "test-worker",
                        Log = new OccurrenceLog
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = level,
                            Message = $"{level} level log",
                            Category = uniqueCategory
                        }
                    }
                ]
            }, cts.Token);
        }

        // Wait for all 5 logs
        var found = await WaitForConditionAsync(
            async () =>
            {
                var occ = await GetOccurrenceAsync(occurrence.Id, cts.Token);
                return occ?.Logs?.Count(l => l.Category == uniqueCategory) >= 5;
            },
            timeout: TimeSpan.FromSeconds(20),
            pollInterval: TimeSpan.FromMilliseconds(100),
            cancellationToken: cts.Token);

        await collector.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("all log levels should be processed");

        var updatedOccurrence = await GetOccurrenceAsync(occurrence.Id, cts.Token);

        foreach (var level in logLevels)
        {
            var log = updatedOccurrence.Logs.FirstOrDefault(l => l.Level == level && l.Category == uniqueCategory);
            log.Should().NotBeNull($"{level} log should exist");
            log!.Message.Should().Be($"{level} level log");
        }
    }

    [Fact]
    public async Task CollectLogs_ShouldIncludeLogData()
    {
        // Arrange
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var job = await SeedScheduledJobAsync("DataLogJob");
        var occurrence = await SeedJobOccurrenceAsync(
            jobId: job.Id,
            jobName: job.JobNameInWorker,
            status: JobOccurrenceStatus.Running
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var uniqueCategory = $"DataTest_{Guid.CreateVersion7():N}";
        var logData = new Dictionary<string, object>
        {
            ["recordsProcessed"] = 100,
            ["duration"] = "5.2s",
            ["success"] = true
        };

        // Act - Start the collector first
        var collector = CreateLogCollectorService();
        _ = Task.Run(async () =>
        {
            try
            {
                await collector.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(3000, cts.Token);

        await PublishLogMessageAsync(new WorkerLogBatchMessage
        {
            BatchTimestamp = DateTime.UtcNow,
            Logs = [
                new WorkerLogMessage
                {
                    OccurrenceId = occurrence.CorrelationId,
                    WorkerId = "test-worker",
                    Log = new OccurrenceLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Information",
                        Message = "Log with data",
                        Category = uniqueCategory,
                        Data = logData
                    }
                }
            ]
        }, cts.Token);

        // Wait for log
        var found = await WaitForConditionAsync(
            async () =>
            {
                var occ = await GetOccurrenceAsync(occurrence.Id, cts.Token);
                return occ?.Logs?.Any(l => l.Category == uniqueCategory) == true;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await collector.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("log with data should be processed");

        var updatedOccurrence = await GetOccurrenceAsync(occurrence.Id, cts.Token);

        var log = updatedOccurrence.Logs.FirstOrDefault(l => l.Category == uniqueCategory);
        log.Should().NotBeNull();
        log!.Data.Should().NotBeNull();
        log.Data.Should().ContainKey("recordsProcessed");
    }

    [Fact]
    public async Task CollectLogs_ShouldPreserveLogTimestampOrder()
    {
        // Arrange
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var job = await SeedScheduledJobAsync("TimestampOrderLogJob");
        var occurrence = await SeedJobOccurrenceAsync(
            jobId: job.Id,
            jobName: job.JobNameInWorker,
            status: JobOccurrenceStatus.Running
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var uniqueCategory = $"OrderTest_{Guid.CreateVersion7():N}";
        var baseTime = DateTime.UtcNow;

        // Act - Start the collector first
        var collector = CreateLogCollectorService();
        _ = Task.Run(async () =>
        {
            try
            {
                await collector.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(3000, cts.Token);

        var logBatch = new WorkerLogBatchMessage
        {
            BatchTimestamp = DateTime.UtcNow,
            Logs = []
        };

        // Send logs with ascending timestamps
        for (int i = 0; i < 3; i++)
        {
            logBatch.Logs.Add(new WorkerLogMessage
            {
                OccurrenceId = occurrence.CorrelationId,
                WorkerId = "test-worker",
                Log = new OccurrenceLog
                {
                    Timestamp = baseTime.AddSeconds(i),
                    Level = "Information",
                    Message = $"Ordered log {i}",
                    Category = uniqueCategory
                },
                MessageTimestamp = DateTime.UtcNow
            });
        }

        await PublishLogMessageAsync(logBatch, cts.Token);

        var found = await WaitForConditionAsync(
            async () =>
            {
                var occ = await GetOccurrenceAsync(occurrence.Id, cts.Token);
                return occ?.Logs?.Count(l => l.Category == uniqueCategory) >= 3;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await collector.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("all ordered logs should be processed");

        var updatedOccurrence = await GetOccurrenceAsync(occurrence.Id, cts.Token);
        var orderedLogs = updatedOccurrence.Logs
            .Where(l => l.Category == uniqueCategory)
            .OrderBy(l => l.Timestamp)
            .ToList();

        orderedLogs.Should().HaveCount(3);
        orderedLogs[0].Message.Should().Be("Ordered log 0");
        orderedLogs[1].Message.Should().Be("Ordered log 1");
        orderedLogs[2].Message.Should().Be("Ordered log 2");
        orderedLogs[0].Timestamp.Should().BeBefore(orderedLogs[1].Timestamp);
        orderedLogs[1].Timestamp.Should().BeBefore(orderedLogs[2].Timestamp);
    }

    [Fact]
    public async Task CollectLogs_ShouldHandleLogWithLongMessage()
    {
        // Arrange
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var job = await SeedScheduledJobAsync("LongMessageLogJob");
        var occurrence = await SeedJobOccurrenceAsync(
            jobId: job.Id,
            jobName: job.JobNameInWorker,
            status: JobOccurrenceStatus.Running
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var uniquePrefix = $"LongMsg_{Guid.CreateVersion7():N}_";
        var longMessage = uniquePrefix + new string('X', 2000);

        // Act
        var collector = CreateLogCollectorService();
        _ = Task.Run(async () =>
        {
            try
            {
                await collector.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(3000, cts.Token);

        await PublishLogMessageAsync(new WorkerLogBatchMessage
        {
            BatchTimestamp = DateTime.UtcNow,
            Logs =
            [
                new()
                {
                    OccurrenceId = occurrence.CorrelationId,
                    WorkerId = "test-worker",
                    Log = new OccurrenceLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Message = longMessage,
                        Category = "LongMsgTest"
                    },
                    MessageTimestamp = DateTime.UtcNow
                }
            ]
        }, cts.Token);

        var found = await WaitForConditionAsync(
            async () =>
            {
                var occ = await GetOccurrenceAsync(occurrence.Id, cts.Token);
                return occ?.Logs?.Any(l => l.Message?.StartsWith(uniquePrefix) == true) == true;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await collector.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("long message log should be processed");

        var updatedOccurrence = await GetOccurrenceAsync(occurrence.Id, cts.Token);
        var log = updatedOccurrence.Logs.First(l => l.Message?.StartsWith(uniquePrefix) == true);
        log.Level.Should().Be("Error");
    }

    private LogCollectorService CreateLogCollectorService() => new(
            _serviceProvider,
            _serviceProvider.GetRequiredService<RabbitMQConnectionFactory>(),
            _serviceProvider.GetRequiredService<IAlertNotifier>(),
            Options.Create(new LogCollectorOptions
            {
                Enabled = true,
                BatchSize = 1,
                BatchIntervalMs = 100 // Faster batch processing for tests
            }),
            _serviceProvider.GetRequiredService<IOptions<RabbitMQOptions>>(),
            _serviceProvider.GetRequiredService<ILoggerFactory>(),
            _serviceProvider.GetRequiredService<BackgroundServiceMetrics>()
        );

    private LogCollectorService CreateDisabledLogCollectorService() => new(
            _serviceProvider,
            _serviceProvider.GetRequiredService<RabbitMQConnectionFactory>(),
            _serviceProvider.GetRequiredService<IAlertNotifier>(),
            Options.Create(new LogCollectorOptions
            {
                Enabled = false,
                BatchSize = 1,
                BatchIntervalMs = 100
            }),
            _serviceProvider.GetRequiredService<IOptions<RabbitMQOptions>>(),
            _serviceProvider.GetRequiredService<ILoggerFactory>(),
            _serviceProvider.GetRequiredService<BackgroundServiceMetrics>()
        );

    private async Task PublishLogMessageAsync(WorkerLogBatchMessage message, CancellationToken cancellationToken)
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

        // Ensure queue exists
        await channel.QueueDeclareAsync(
            queue: WorkerConstant.Queues.WorkerLogs,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, ConstantJsonOptions.PropNameCaseInsensitive));

        var properties = new BasicProperties
        {
            Persistent = true
        };

        await channel.BasicPublishAsync(
            exchange: "",
            routingKey: WorkerConstant.Queues.WorkerLogs,
            mandatory: false,
            basicProperties: properties,
            body: body,
            cancellationToken: cancellationToken);
    }

    private async Task PublishRawLogMessageAsync(byte[] body, CancellationToken cancellationToken)
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

        await channel.QueueDeclareAsync(
            queue: WorkerConstant.Queues.WorkerLogs,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        var properties = new BasicProperties
        {
            Persistent = true
        };

        await channel.BasicPublishAsync(
            exchange: "",
            routingKey: WorkerConstant.Queues.WorkerLogs,
            mandatory: false,
            basicProperties: properties,
            body: body,
            cancellationToken: cancellationToken);
    }

    #region Negative / Edge-case Scenarios

    [Fact]
    public async Task CollectLogs_ShouldNotProcess_WhenDisabledInOptions()
    {
        // Arrange
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var job = await SeedScheduledJobAsync("DisabledCollectorJob");
        var occurrence = await SeedJobOccurrenceAsync(
            jobId: job.Id,
            jobName: job.JobNameInWorker,
            status: JobOccurrenceStatus.Running
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var uniqueMessage = $"Should not appear {Guid.CreateVersion7():N}";

        // Publish log message before starting disabled collector
        await PublishLogMessageAsync(new WorkerLogBatchMessage
        {
            BatchTimestamp = DateTime.UtcNow,
            Logs =
            [
                new()
                {
                    OccurrenceId = occurrence.CorrelationId,
                    WorkerId = "test-worker",
                    Log = new OccurrenceLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Information",
                        Message = uniqueMessage,
                        Category = "DisabledTest"
                    },
                    MessageTimestamp = DateTime.UtcNow
                }
            ]
        }, cts.Token);

        // Act - Start disabled collector
        var collector = CreateDisabledLogCollectorService();
        _ = Task.Run(async () =>
        {
            try
            {
                await collector.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(3000, cts.Token);

        await collector.StopAsync(cts.Token);

        // Assert - No logs should be processed
        var updatedOccurrence = await GetOccurrenceAsync(occurrence.Id, cts.Token);
        updatedOccurrence.Logs.Should().NotContain(l => l.Message == uniqueMessage,
            "disabled collector should not process any messages");
    }

    [Fact]
    public async Task CollectLogs_ShouldHandleInvalidMessage_WithoutCrashing()
    {
        // Arrange
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var job = await SeedScheduledJobAsync("InvalidMsgJob");
        var occurrence = await SeedJobOccurrenceAsync(
            jobId: job.Id,
            jobName: job.JobNameInWorker,
            status: JobOccurrenceStatus.Running
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var uniqueMessage = $"Valid after invalid {Guid.CreateVersion7():N}";

        // Act - Start collector
        var collector = CreateLogCollectorService();
        _ = Task.Run(async () =>
        {
            try
            {
                await collector.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(3000, cts.Token);

        // Publish invalid (garbage) message
        await PublishRawLogMessageAsync("this is not valid json!!!@#$%"u8.ToArray(), cts.Token);

        // Publish a valid message after the invalid one
        await PublishLogMessageAsync(new WorkerLogBatchMessage
        {
            BatchTimestamp = DateTime.UtcNow,
            Logs =
            [
                new()
                {
                    OccurrenceId = occurrence.CorrelationId,
                    WorkerId = "test-worker",
                    Log = new OccurrenceLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Information",
                        Message = uniqueMessage,
                        Category = "InvalidTest"
                    },
                    MessageTimestamp = DateTime.UtcNow
                }
            ]
        }, cts.Token);

        // Wait for the valid message to be processed (proves collector didn't crash)
        var found = await WaitForConditionAsync(
            async () =>
            {
                var occ = await GetOccurrenceAsync(occurrence.Id, cts.Token);
                return occ?.Logs?.Any(l => l.Message == uniqueMessage) == true;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await collector.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("collector should recover from invalid message and process subsequent valid messages");
    }

    [Fact]
    public async Task CollectLogs_ShouldHandleEmptyBatchMessage()
    {
        // Arrange
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var job = await SeedScheduledJobAsync("EmptyBatchJob");
        var occurrence = await SeedJobOccurrenceAsync(
            jobId: job.Id,
            jobName: job.JobNameInWorker,
            status: JobOccurrenceStatus.Running
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var uniqueMessage = $"Valid after empty batch {Guid.CreateVersion7():N}";

        // Act - Start collector
        var collector = CreateLogCollectorService();
        _ = Task.Run(async () =>
        {
            try
            {
                await collector.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(3000, cts.Token);

        // Publish empty batch message (Count = 0)
        await PublishLogMessageAsync(new WorkerLogBatchMessage
        {
            BatchTimestamp = DateTime.UtcNow,
            Logs = [] // Empty
        }, cts.Token);

        // Publish a valid message after the empty one
        await PublishLogMessageAsync(new WorkerLogBatchMessage
        {
            BatchTimestamp = DateTime.UtcNow,
            Logs =
            [
                new()
                {
                    OccurrenceId = occurrence.CorrelationId,
                    WorkerId = "test-worker",
                    Log = new OccurrenceLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Information",
                        Message = uniqueMessage,
                        Category = "EmptyBatchTest"
                    },
                    MessageTimestamp = DateTime.UtcNow
                }
            ]
        }, cts.Token);

        // Wait for valid message to be processed
        var found = await WaitForConditionAsync(
            async () =>
            {
                var occ = await GetOccurrenceAsync(occurrence.Id, cts.Token);
                return occ?.Logs?.Any(l => l.Message == uniqueMessage) == true;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await collector.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("collector should handle empty batch and continue processing");
    }

    [Fact]
    public async Task CollectLogs_ShouldEventuallyInsert_WhenOccurrenceCreatedAfterLogArrives()
    {
        // Arrange
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var job = await SeedScheduledJobAsync("PendingLogJob");

        // Create a correlation ID for an occurrence that doesn't exist yet
        var futureCorrelationId = Guid.CreateVersion7();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var uniqueMessage = $"Pending log {Guid.CreateVersion7():N}";

        // Act - Start collector
        var collector = CreateLogCollectorService();
        _ = Task.Run(async () =>
        {
            try
            {
                await collector.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(3000, cts.Token);

        // Publish log for non-existent occurrence (will trigger FK violation ? pending queue)
        await PublishLogMessageAsync(new WorkerLogBatchMessage
        {
            BatchTimestamp = DateTime.UtcNow,
            Logs =
            [
                new()
                {
                    OccurrenceId = futureCorrelationId,
                    WorkerId = "test-worker",
                    Log = new OccurrenceLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Information",
                        Message = uniqueMessage,
                        Category = "PendingTest"
                    },
                    MessageTimestamp = DateTime.UtcNow
                }
            ]
        }, cts.Token);

        // Wait a bit for the FK violation to occur and log to move to pending queue
        await Task.Delay(2000, cts.Token);

        // Now create the occurrence (simulates dispatcher creating it after log arrived)
        var dbContext = GetDbContext();
        var occurrence = new JobOccurrence
        {
            Id = futureCorrelationId,
            JobId = job.Id,
            JobName = job.JobNameInWorker,
            JobVersion = 1,
            Status = JobOccurrenceStatus.Running,
            CreatedAt = DateTime.UtcNow,
            CreatorUserName = "TestUser"
        };

        await dbContext.JobOccurrences.AddAsync(occurrence, cts.Token);
        await dbContext.SaveChangesAsync(cts.Token);

        // Wait for the pending log to be retried and inserted
        var found = await WaitForConditionAsync(
            async () =>
            {
                var occ = await GetOccurrenceAsync(futureCorrelationId, cts.Token);
                return occ?.Logs?.Any(l => l.Message == uniqueMessage) == true;
            },
            timeout: TimeSpan.FromSeconds(20),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await collector.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("pending log should be retried and inserted after occurrence is created");

        var updatedOccurrence = await GetOccurrenceAsync(futureCorrelationId, cts.Token);
        var pendingLog = updatedOccurrence.Logs.FirstOrDefault(l => l.Message == uniqueMessage);
        pendingLog.Should().NotBeNull();
        pendingLog!.Category.Should().Be("PendingTest");
    }

    [Fact]
    public async Task CollectLogs_ShouldPreserveExceptionType()
    {
        // Arrange
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var job = await SeedScheduledJobAsync("ExceptionLogJob");
        var occurrence = await SeedJobOccurrenceAsync(
            jobId: job.Id,
            jobName: job.JobNameInWorker,
            status: JobOccurrenceStatus.Running
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var uniqueCategory = $"ExceptionTest_{Guid.CreateVersion7():N}";

        // Act - Start collector
        var collector = CreateLogCollectorService();
        _ = Task.Run(async () =>
        {
            try
            {
                await collector.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(3000, cts.Token);

        await PublishLogMessageAsync(new WorkerLogBatchMessage
        {
            BatchTimestamp = DateTime.UtcNow,
            Logs =
            [
                new()
                {
                    OccurrenceId = occurrence.CorrelationId,
                    WorkerId = "test-worker",
                    Log = new OccurrenceLog
                    {
                        Timestamp = DateTime.UtcNow,
                        Level = "Error",
                        Message = "NullReferenceException: Object reference not set",
                        Category = uniqueCategory,
                        ExceptionType = "System.NullReferenceException"
                    },
                    MessageTimestamp = DateTime.UtcNow
                }
            ]
        }, cts.Token);

        var found = await WaitForConditionAsync(
            async () =>
            {
                var occ = await GetOccurrenceAsync(occurrence.Id, cts.Token);
                return occ?.Logs?.Any(l => l.Category == uniqueCategory) == true;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await collector.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("log with exception type should be processed");

        var updatedOccurrence = await GetOccurrenceAsync(occurrence.Id, cts.Token);
        var exLog = updatedOccurrence.Logs.First(l => l.Category == uniqueCategory);
        exLog.ExceptionType.Should().Be("System.NullReferenceException");
        exLog.Level.Should().Be("Error");
    }

    [Fact]
    public async Task CollectLogs_ShouldHandleSingleMessageFormat()
    {
        // Arrange
        await InitializeAsync();
        await PurgeAllQueuesAsync();

        var job = await SeedScheduledJobAsync("SingleMsgFormatJob");
        var occurrence = await SeedJobOccurrenceAsync(
            jobId: job.Id,
            jobName: job.JobNameInWorker,
            status: JobOccurrenceStatus.Running
        );

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var uniqueMessage = $"Single format log {Guid.CreateVersion7():N}";

        // Act - Start collector
        var collector = CreateLogCollectorService();
        _ = Task.Run(async () =>
        {
            try
            {
                await collector.StartAsync(cts.Token);
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(3000, cts.Token);

        // Publish as single WorkerLogMessage (not batch) - backward compatibility path
        var singleMessage = new WorkerLogMessage
        {
            OccurrenceId = occurrence.CorrelationId,
            WorkerId = "test-worker",
            Log = new OccurrenceLog
            {
                Timestamp = DateTime.UtcNow,
                Level = "Warning",
                Message = uniqueMessage,
                Category = "SingleFormatTest"
            },
            MessageTimestamp = DateTime.UtcNow
        };

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(singleMessage, ConstantJsonOptions.PropNameCaseInsensitive));
        await PublishRawLogMessageAsync(body, cts.Token);

        // Wait for log to be processed
        var found = await WaitForConditionAsync(
            async () =>
            {
                var occ = await GetOccurrenceAsync(occurrence.Id, cts.Token);
                return occ?.Logs?.Any(l => l.Message == uniqueMessage) == true;
            },
            timeout: TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: cts.Token);

        await collector.StopAsync(cts.Token);

        // Assert
        found.Should().BeTrue("single message format (backward compat) should be processed");

        var updatedOccurrence = await GetOccurrenceAsync(occurrence.Id, cts.Token);
        var log = updatedOccurrence.Logs.First(l => l.Message == uniqueMessage);
        log.Level.Should().Be("Warning");
        log.Category.Should().Be("SingleFormatTest");
    }

    #endregion
}
