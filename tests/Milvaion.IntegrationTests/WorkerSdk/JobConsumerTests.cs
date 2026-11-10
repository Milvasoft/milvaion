using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Utils;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Milvasoft.Milvaion.Sdk.Worker.Core;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using Milvasoft.Milvaion.Sdk.Worker.RabbitMQ;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.WorkerSdk;

/// <summary>
/// Integration tests for JobConsumer.
/// Tests RabbitMQ message consumption, job execution, retry logic, and DLQ routing.
/// </summary>
[Collection(nameof(WorkerSdkTestCollection))]
public class JobConsumerTests(WorkerSdkContainerFixture fixture, ITestOutputHelper output) : WorkerSdkTestBase(fixture, output)
{
    private const string _testRoutingPattern = "test-consumer.*";
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    #region Job Resolution

    [Fact]
    public async Task JobConsumer_ShouldResolveCorrectJobType_WhenMultipleJobsRegistered()
    {
        // Arrange

        var services = new ServiceCollection();
        services.AddTransient<IJobBase, SuccessAsyncJob>();
        services.AddTransient<IJobBase, AnotherAsyncJob>();
        services.AddScoped<IMilvaLogger>(sp => sp.GetRequiredService<ILoggerFactory>().CreateMilvaLogger<IMilvaLogger>());
        services.AddSingleton<ILoggerFactory>(GetLoggerFactory());
        services.AddScoped<JobExecutor>();
        services.AddSingleton<WorkerJobTracker>();

        var sp = services.BuildServiceProvider();

        var jobs = sp.GetServices<IJobBase>().ToList();

        // Assert
        jobs.Should().HaveCount(2);
        jobs.Should().Contain(j => j.GetType().Name == nameof(SuccessAsyncJob));
        jobs.Should().Contain(j => j.GetType().Name == nameof(AnotherAsyncJob));
    }

    #endregion

    #region JobExecutor Integration with JobConsumer Context

    [Fact]
    public async Task JobExecutor_ShouldReturnCompleted_WhenAsyncJobSucceeds()
    {
        // Arrange

        var loggerFactory = GetLoggerFactory();
        var executor = new JobExecutor(loggerFactory);
        var job = new SuccessAsyncJob();
        var correlationId = Guid.CreateVersion7();
        var scheduledJob = CreateScheduledJob(nameof(SuccessAsyncJob));
        var workerOptions = CreateWorkerOptions();
        var config = new JobConsumerConfig { ExecutionTimeoutSeconds = 30 };

        // Act
        var result = await executor.ExecuteAsync(job, scheduledJob, correlationId, null, workerOptions, config, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be(JobOccurrenceStatus.Completed);
        result.CorrelationId.Should().Be(correlationId);
        result.DurationMs.Should().BeGreaterOrEqualTo(0);
        result.Exception.Should().BeNull();
    }

    [Fact]
    public async Task JobExecutor_ShouldReturnFailed_WhenJobThrowsException()
    {
        // Arrange

        var loggerFactory = GetLoggerFactory();
        var executor = new JobExecutor(loggerFactory);
        var job = new FailingAsyncJob();
        var correlationId = Guid.CreateVersion7();
        var scheduledJob = CreateScheduledJob(nameof(FailingAsyncJob));
        var workerOptions = CreateWorkerOptions();
        var config = new JobConsumerConfig { ExecutionTimeoutSeconds = 30 };

        // Act
        var result = await executor.ExecuteAsync(job, scheduledJob, correlationId, null, workerOptions, config, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be(JobOccurrenceStatus.Failed);
        result.Exception.Should().Contain("Simulated job failure");
        result.IsPermanentFailure.Should().BeFalse();
    }

    [Fact]
    public async Task JobExecutor_ShouldReturnTimedOut_WhenJobExceedsTimeout()
    {
        // Arrange

        var loggerFactory = GetLoggerFactory();
        var executor = new JobExecutor(loggerFactory);
        var job = new SlowAsyncJob();
        var correlationId = Guid.CreateVersion7();
        var scheduledJob = CreateScheduledJob(nameof(SlowAsyncJob));
        var workerOptions = CreateWorkerOptions();
        var config = new JobConsumerConfig { ExecutionTimeoutSeconds = 1 }; // 1 second timeout

        // Act
        var result = await executor.ExecuteAsync(job, scheduledJob, correlationId, null, workerOptions, config, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be(JobOccurrenceStatus.TimedOut);
        result.Exception.Should().Contain("timeout");
    }

    [Fact]
    public async Task JobExecutor_ShouldReturnPermanentFailure_WhenPermanentJobExceptionThrown()
    {
        // Arrange

        var loggerFactory = GetLoggerFactory();
        var executor = new JobExecutor(loggerFactory);
        var job = new PermanentFailureJob();
        var correlationId = Guid.CreateVersion7();
        var scheduledJob = CreateScheduledJob(nameof(PermanentFailureJob));
        var workerOptions = CreateWorkerOptions();
        var config = new JobConsumerConfig { ExecutionTimeoutSeconds = 30 };

        // Act
        var result = await executor.ExecuteAsync(job, scheduledJob, correlationId, null, workerOptions, config, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be(JobOccurrenceStatus.Failed);
        result.IsPermanentFailure.Should().BeTrue();
        result.Exception.Should().Contain("Invalid job data");
    }

    [Fact]
    public async Task JobExecutor_ShouldReturnCancelled_WhenCancellationRequested()
    {
        // Arrange

        var loggerFactory = GetLoggerFactory();
        var executor = new JobExecutor(loggerFactory);
        var job = new CancellationAwareJob();
        var correlationId = Guid.CreateVersion7();
        var scheduledJob = CreateScheduledJob(nameof(CancellationAwareJob));
        var workerOptions = CreateWorkerOptions();
        var config = new JobConsumerConfig { ExecutionTimeoutSeconds = 0 }; // No timeout

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act
        var result = await executor.ExecuteAsync(job, scheduledJob, correlationId, null, workerOptions, config, cts.Token);

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be(JobOccurrenceStatus.Cancelled);
    }

    [Fact]
    public async Task JobExecutor_ShouldReturnResult_WhenAsyncJobWithResultSucceeds()
    {
        // Arrange
        var loggerFactory = GetLoggerFactory();
        var executor = new JobExecutor(loggerFactory);
        var job = new AsyncJobWithResultImpl();
        var correlationId = Guid.CreateVersion7();
        var scheduledJob = CreateScheduledJob(nameof(AsyncJobWithResultImpl));
        var workerOptions = CreateWorkerOptions();
        var config = new JobConsumerConfig { ExecutionTimeoutSeconds = 30 };

        // Act
        var result = await executor.ExecuteAsync(job, scheduledJob, correlationId, null, workerOptions, config, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().Be(JobOccurrenceStatus.Completed);
        result.Result.Should().Be("Custom result from job");
    }

    #endregion

    #region RabbitMQ Message Publishing (simulating dispatcher -> consumer flow)

    [Fact]
    public async Task RabbitMQ_ShouldPublishAndConsumeMessage()
    {
        // Arrange
        await PurgeTestQueuesAsync();

        var rabbitFactory = new ConnectionFactory
        {
            HostName = GetRabbitMqHost(),
            Port = GetRabbitMqPort(),
            UserName = "guest",
            Password = "guest"
        };

        var exchangeName = WorkerConstant.ExchangeName;
        var routingKey = "test-consumer.testjob";
        var queueName = $"{WorkerConstant.Queues.Jobs}.test-consumer.wildcard";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var connection = await rabbitFactory.CreateConnectionAsync(cts.Token);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cts.Token);

        // Declare exchange and queue
        await channel.ExchangeDeclareAsync(exchange: exchangeName, type: "topic", durable: true, cancellationToken: cts.Token);
        await channel.QueueDeclareAsync(queue: queueName, durable: true, exclusive: false, autoDelete: false, cancellationToken: cts.Token);
        await channel.QueueBindAsync(queue: queueName, exchange: exchangeName, routingKey: _testRoutingPattern, cancellationToken: cts.Token);

        // Publish a test job message
        var correlationId = Guid.CreateVersion7();
        var testJob = CreateScheduledJob(nameof(SuccessAsyncJob));
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(testJob));
        var properties = new BasicProperties
        {
            Persistent = true,
            CorrelationId = correlationId.ToString(),
            Headers = new Dictionary<string, object>
            {
                { "CorrelationId", Encoding.UTF8.GetBytes(correlationId.ToString()) }
            }
        };

        await channel.BasicPublishAsync(exchange: exchangeName, routingKey: routingKey, mandatory: false, basicProperties: properties, body: body, cancellationToken: cts.Token);

        // Consume the message
        var messageReceived = false;
        ScheduledJob receivedJob = null;

        var getResult = await channel.BasicGetAsync(queueName, autoAck: true, cts.Token);

        if (getResult != null)
        {
            messageReceived = true;
            receivedJob = JsonSerializer.Deserialize<ScheduledJob>(getResult.Body.Span, _jsonOptions);
        }

        // Assert
        messageReceived.Should().BeTrue("message should be available in queue");
        receivedJob.Should().NotBeNull();
        receivedJob.JobNameInWorker.Should().Be(nameof(SuccessAsyncJob));
    }

    [Fact]
    public async Task RabbitMQ_DLQ_ShouldReceiveNackedMessages()
    {
        // Arrange
        await PurgeTestQueuesAsync();

        var rabbitFactory = new ConnectionFactory
        {
            HostName = GetRabbitMqHost(),
            Port = GetRabbitMqPort(),
            UserName = "guest",
            Password = "guest"
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var connection = await rabbitFactory.CreateConnectionAsync(cts.Token);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cts.Token);

        // Setup DLX and DLQ
        await channel.ExchangeDeclareAsync(exchange: WorkerConstant.DeadLetterExchangeName, type: "direct", durable: true, cancellationToken: cts.Token);
        await channel.QueueDeclareAsync(queue: WorkerConstant.Queues.FailedOccurrences, durable: true, exclusive: false, autoDelete: false, cancellationToken: cts.Token);
        await channel.QueueBindAsync(queue: WorkerConstant.Queues.FailedOccurrences, exchange: WorkerConstant.DeadLetterExchangeName, routingKey: WorkerConstant.DeadLetterRoutingKey, cancellationToken: cts.Token);

        // Setup main queue with DLX
        var dlqQueueName = "test-dlq-source-queue";
        var queueArgs = new Dictionary<string, object>
        {
            { "x-dead-letter-exchange", WorkerConstant.DeadLetterExchangeName },
            { "x-dead-letter-routing-key", WorkerConstant.DeadLetterRoutingKey }
        };

        await channel.QueueDeclareAsync(queue: dlqQueueName, durable: true, exclusive: false, autoDelete: false, arguments: queueArgs, cancellationToken: cts.Token);

        // Publish a message to the main queue
        var body = Encoding.UTF8.GetBytes("test-dlq-message");
        await channel.BasicPublishAsync(exchange: "", routingKey: dlqQueueName, body: body, cancellationToken: cts.Token);

        // Get and NACK the message (send to DLQ)
        var getResult = await channel.BasicGetAsync(dlqQueueName, autoAck: false, cts.Token);
        getResult.Should().NotBeNull();

        await channel.BasicNackAsync(getResult.DeliveryTag, multiple: false, requeue: false, cts.Token);

        // Wait a moment for DLQ processing
        await Task.Delay(500, cts.Token);

        // Check DLQ
        var dlqResult = await channel.BasicGetAsync(WorkerConstant.Queues.FailedOccurrences, autoAck: true, cts.Token);

        // Assert
        dlqResult.Should().NotBeNull("message should be routed to DLQ after NACK");
    }

    #endregion

    #region WorkerJobTracker Integration

    [Fact]
    public async Task WorkerJobTracker_ShouldTrackConcurrentJobs()
    {
        // Arrange

        var loggerFactory = GetLoggerFactory();
        var tracker = new WorkerJobTracker(loggerFactory);
        var workerId = "integration-test-worker";

        // Act - simulate multiple concurrent jobs
        tracker.IncrementJobCount(workerId);
        tracker.IncrementJobCount(workerId);
        tracker.IncrementJobCount(workerId);

        var peakCount = tracker.GetJobCount(workerId);

        tracker.DecrementJobCount(workerId);
        var afterDecrement = tracker.GetJobCount(workerId);

        // Assert
        peakCount.Should().Be(3);
        afterDecrement.Should().Be(2);
    }

    [Fact]
    public async Task WorkerJobTracker_ShouldTrackMultipleWorkers()
    {
        // Arrange

        var loggerFactory = GetLoggerFactory();
        var tracker = new WorkerJobTracker(loggerFactory);

        // Act
        tracker.IncrementJobCount("worker-1");
        tracker.IncrementJobCount("worker-1");
        tracker.IncrementJobCount("worker-2");

        var allCounts = tracker.GetAllJobCounts();

        // Assert
        allCounts.Should().HaveCount(2);
        allCounts["worker-1"].Should().Be(2);
        allCounts["worker-2"].Should().Be(1);
    }

    #endregion

    #region Helper Methods

    private static ScheduledJob CreateScheduledJob(string jobName) => new()
    {
        Id = Guid.CreateVersion7(),
        DisplayName = $"Test {jobName}",
        JobNameInWorker = jobName,
        ExecuteAt = DateTime.UtcNow,
        IsActive = true
    };

    private WorkerOptions CreateWorkerOptions()
    {
        var options = new WorkerOptions
        {
            WorkerId = "test-worker",
            MaxParallelJobs = 5,
            Heartbeat = new HeartbeatSettings { JobHeartbeatIntervalSeconds = 0 },
            RabbitMQ = new RabbitMQSettings
            {
                Host = GetRabbitMqHost(),
                Port = GetRabbitMqPort(),
                Username = "guest",
                Password = "guest",
                VirtualHost = "/",
                RoutingKeyPattern = _testRoutingPattern
            }
        };

        options.RegenerateInstanceId();

        return options;
    }

    #endregion

    #region Full JobConsumer Integration Tests

    [Fact]
    public async Task JobConsumer_ShouldConsumeAndExecuteJob_EndToEnd()
    {
        // Arrange
        await PurgeTestQueuesAsync();

        var executionFlag = new TaskCompletionSource<bool>();
        var services = BuildJobConsumerServiceProvider(sp =>
        {
            sp.AddTransient<IJobBase>(__ => new CallbackAsyncJob(() => executionFlag.TrySetResult(true)));
        });

        var sp = services.BuildServiceProvider();
        var workerOptions = CreateWorkerOptions();
        var consumer = new JobConsumer(
            sp,
            Microsoft.Extensions.Options.Options.Create(workerOptions),
            Microsoft.Extensions.Options.Options.Create(new JobConsumerOptions()),
            sp.GetRequiredService<IMilvaLogger>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Start consumer
        _ = Task.Run(async () =>
        {
            try
            {
                await consumer.StartAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(2000, cts.Token);

        // Act - Publish a job message to the topic exchange
        var correlationId = Guid.CreateVersion7();
        var job = CreateScheduledJob(nameof(CallbackAsyncJob));
        await PublishJobToExchangeAsync(job, correlationId, "test-consumer.callbackjob", cts.Token);

        // Assert - Wait for the job to be executed
        var completed = await Task.WhenAny(executionFlag.Task, Task.Delay(TimeSpan.FromSeconds(15), cts.Token));

        await consumer.StopAsync(CancellationToken.None);

        executionFlag.Task.IsCompletedSuccessfully.Should().BeTrue("job should be consumed and executed by JobConsumer");
    }

    [Fact]
    public async Task JobConsumer_ShouldRequeueRunningJob_OnShutdown()
    {
        // Arrange
        await PurgeTestQueuesAsync();

        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var services = BuildJobConsumerServiceProvider(sp =>
        {
            sp.AddTransient<IJobBase>(__ => new CallbackAsyncJob(async () =>
            {
                started.TrySetResult(true);
                await Task.Delay(TimeSpan.FromMinutes(1), CurrentJobToken.Value);
            }));
        });

        var sp = services.BuildServiceProvider();
        var consumer = new JobConsumer(sp,
                                       Microsoft.Extensions.Options.Options.Create(CreateWorkerOptions()),
                                       Microsoft.Extensions.Options.Options.Create(new JobConsumerOptions()),
                                       sp.GetRequiredService<IMilvaLogger>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await consumer.StartAsync(cts.Token);
        await Task.Delay(2000, cts.Token);

        await PublishJobToExchangeAsync(CreateScheduledJob(nameof(CallbackAsyncJob)), Guid.CreateVersion7(), "test-consumer.callbackjob", cts.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(15), cts.Token);

        // Act
        await consumer.StopAsync(CancellationToken.None);

        // Assert - back on its queue for the next worker, and not dead-lettered
        var rabbitFactory = new ConnectionFactory { HostName = GetRabbitMqHost(), Port = GetRabbitMqPort(), UserName = "guest", Password = "guest" };

        await using var connection = await rabbitFactory.CreateConnectionAsync(cts.Token);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cts.Token);

        var requeued = await channel.BasicGetAsync($"{WorkerConstant.Queues.Jobs}.test-consumer.wildcard", autoAck: true, cts.Token);
        var deadLettered = await channel.BasicGetAsync(WorkerConstant.Queues.FailedOccurrences, autoAck: true, cts.Token);

        requeued.Should().NotBeNull("a job interrupted by shutdown should be returned to the queue");
        requeued!.Redelivered.Should().BeTrue();
        deadLettered.Should().BeNull();
    }

    [Fact]
    public async Task JobConsumer_ShouldAckMessage_WhenJobSucceeds()
    {
        // Arrange
        await PurgeTestQueuesAsync();

        var services = BuildJobConsumerServiceProvider(sp =>
        {
            sp.AddTransient<IJobBase, SuccessAsyncJob>();
        });

        var sp = services.BuildServiceProvider();
        var workerOptions = CreateWorkerOptions();
        var consumer = new JobConsumer(
            sp,
            Microsoft.Extensions.Options.Options.Create(workerOptions),
            Microsoft.Extensions.Options.Options.Create(new JobConsumerOptions()),
            sp.GetRequiredService<IMilvaLogger>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        _ = Task.Run(async () =>
        {
            try
            {
                await consumer.StartAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(2000, cts.Token);

        // Act
        var correlationId = Guid.CreateVersion7();
        var job = CreateScheduledJob(nameof(SuccessAsyncJob));
        await PublishJobToExchangeAsync(job, correlationId, "test-consumer.successjob", cts.Token);

        // Wait for processing
        await Task.Delay(3000, cts.Token);

        await consumer.StopAsync(CancellationToken.None);

        // Assert - Queue should be empty (message was ACK'd)
        var rabbitFactory = new ConnectionFactory
        {
            HostName = GetRabbitMqHost(),
            Port = GetRabbitMqPort(),
            UserName = "guest",
            Password = "guest"
        };

        await using var connection = await rabbitFactory.CreateConnectionAsync(cts.Token);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cts.Token);

        var queueName = $"{WorkerConstant.Queues.Jobs}.test-consumer.wildcard";
        var remaining = await channel.BasicGetAsync(queueName, autoAck: false, cts.Token);
        remaining.Should().BeNull("queue should be empty after successful job ACK");
    }

    [Fact]
    public async Task JobConsumer_ShouldNackBadMessage_AndRouteToDLQ()
    {
        // Arrange
        await PurgeTestQueuesAsync();

        var services = BuildJobConsumerServiceProvider(sp =>
        {
            sp.AddTransient<IJobBase, SuccessAsyncJob>();
        });

        var sp = services.BuildServiceProvider();
        var workerOptions = CreateWorkerOptions();
        var consumer = new JobConsumer(
            sp,
            Microsoft.Extensions.Options.Options.Create(workerOptions),
            Microsoft.Extensions.Options.Options.Create(new JobConsumerOptions()),
            sp.GetRequiredService<IMilvaLogger>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        _ = Task.Run(async () =>
        {
            try
            {
                await consumer.StartAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(2000, cts.Token);

        // Act - Publish an invalid (non-JSON) message to the queue
        var rabbitFactory = new ConnectionFactory
        {
            HostName = GetRabbitMqHost(),
            Port = GetRabbitMqPort(),
            UserName = "guest",
            Password = "guest"
        };

        await using var connection = await rabbitFactory.CreateConnectionAsync(cts.Token);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cts.Token);

        var queueName = $"{WorkerConstant.Queues.Jobs}.test-consumer.wildcard";
        var body = Encoding.UTF8.GetBytes("NOT-VALID-JSON!!!");
        var properties = new BasicProperties { Persistent = true };

        await channel.BasicPublishAsync(
            exchange: "",
            routingKey: queueName,
            mandatory: false,
            basicProperties: properties,
            body: body,
            cancellationToken: cts.Token);

        // Wait for consumer to process and NACK the bad message
        await Task.Delay(3000, cts.Token);

        await consumer.StopAsync(CancellationToken.None);

        // Assert - Message should land in DLQ
        var dlqResult = await channel.BasicGetAsync(WorkerConstant.Queues.FailedOccurrences, autoAck: true, cts.Token);
        dlqResult.Should().NotBeNull("invalid message should be routed to DLQ");
    }

    [Fact]
    public async Task JobConsumer_ShouldHandleConcurrentJobs()
    {
        // Arrange
        await PurgeTestQueuesAsync();

        var completedCount = 0;
        var concurrentPeak = 0;
        var currentConcurrent = 0;
        var lockObj = new object();

        var services = BuildJobConsumerServiceProvider(sp =>
        {
            sp.AddTransient<IJobBase>(__ => new CallbackAsyncJob(async () =>
            {
                var current = Interlocked.Increment(ref currentConcurrent);

                lock (lockObj)
                {
                    if (current > concurrentPeak)
                        concurrentPeak = current;
                }

                await Task.Delay(500); // Simulate some work

                Interlocked.Decrement(ref currentConcurrent);
                Interlocked.Increment(ref completedCount);
            }));
        });

        var sp = services.BuildServiceProvider();
        var workerOptions = CreateWorkerOptions();
        workerOptions = new WorkerOptions
        {
            WorkerId = "test-worker",
            MaxParallelJobs = 3,
            Heartbeat = new HeartbeatSettings { JobHeartbeatIntervalSeconds = 0 },
            RabbitMQ = new RabbitMQSettings
            {
                Host = GetRabbitMqHost(),
                Port = GetRabbitMqPort(),
                Username = "guest",
                Password = "guest",
                VirtualHost = "/",
                RoutingKeyPattern = _testRoutingPattern
            }
        };
        workerOptions.RegenerateInstanceId();

        var consumer = new JobConsumer(
            sp,
            Microsoft.Extensions.Options.Options.Create(workerOptions),
            Microsoft.Extensions.Options.Options.Create(new JobConsumerOptions()),
            sp.GetRequiredService<IMilvaLogger>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        _ = Task.Run(async () =>
        {
            try
            {
                await consumer.StartAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(2000, cts.Token);

        // Act - Publish 5 jobs rapidly
        for (int i = 0; i < 5; i++)
        {
            var correlationId = Guid.CreateVersion7();
            var job = CreateScheduledJob(nameof(CallbackAsyncJob));
            await PublishJobToExchangeAsync(job, correlationId, "test-consumer.callbackjob", cts.Token);
        }

        // Wait for all jobs to complete
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (completedCount < 5 && DateTime.UtcNow < deadline)
            await Task.Delay(200, cts.Token);

        await consumer.StopAsync(CancellationToken.None);

        // Assert
        completedCount.Should().Be(5, "all 5 jobs should be completed");
        concurrentPeak.Should().BeGreaterThan(1, "jobs should run concurrently");
        concurrentPeak.Should().BeLessOrEqualTo(3, "concurrency should respect MaxParallelJobs=3");
    }

    [Fact]
    public async Task JobConsumer_ShouldNackFailedJob_WhenNoJobTypeRegistered()
    {
        // Arrange
        await PurgeTestQueuesAsync();

        var services = BuildJobConsumerServiceProvider(_ => { });

        var sp = services.BuildServiceProvider();
        var workerOptions = CreateWorkerOptions();
        var consumer = new JobConsumer(
            sp,
            Microsoft.Extensions.Options.Options.Create(workerOptions),
            Microsoft.Extensions.Options.Options.Create(new JobConsumerOptions()),
            sp.GetRequiredService<IMilvaLogger>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        _ = Task.Run(async () =>
        {
            try
            {
                await consumer.StartAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(2000, cts.Token);

        // Act - Publish a job that has no registered handler
        var correlationId = Guid.CreateVersion7();
        var job = CreateScheduledJob("UnregisteredJob");
        await PublishJobToExchangeAsync(job, correlationId, "test-consumer.unregistered", cts.Token);

        // Wait for processing
        await Task.Delay(3000, cts.Token);

        await consumer.StopAsync(CancellationToken.None);

        // Assert - Message should be NACK'd and end up in DLQ
        var rabbitFactory = new ConnectionFactory
        {
            HostName = GetRabbitMqHost(),
            Port = GetRabbitMqPort(),
            UserName = "guest",
            Password = "guest"
        };

        await using var connection = await rabbitFactory.CreateConnectionAsync(cts.Token);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cts.Token);

        var dlqResult = await channel.BasicGetAsync(WorkerConstant.Queues.FailedOccurrences, autoAck: true, cts.Token);
        dlqResult.Should().NotBeNull("unresolvable job should be routed to DLQ");
    }

    [Fact]
    public async Task JobConsumer_ShouldParseCorrelationId_FromHeaders()
    {
        // Arrange
        await PurgeTestQueuesAsync();

        Guid? capturedCorrelationId = null;
        var executionFlag = new TaskCompletionSource<bool>();

        var services = BuildJobConsumerServiceProvider(sp =>
        {
            sp.AddTransient<IJobBase>(__ => new CorrelationCaptureJob(cid =>
            {
                capturedCorrelationId = cid;
                executionFlag.TrySetResult(true);
            }));
        });

        var sp = services.BuildServiceProvider();
        var workerOptions = CreateWorkerOptions();
        var consumer = new JobConsumer(
            sp,
            Microsoft.Extensions.Options.Options.Create(workerOptions),
            Microsoft.Extensions.Options.Options.Create(new JobConsumerOptions()),
            sp.GetRequiredService<IMilvaLogger>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        _ = Task.Run(async () =>
        {
            try
            {
                await consumer.StartAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(2000, cts.Token);

        // Act
        var expectedCorrelationId = Guid.CreateVersion7();
        var job = CreateScheduledJob(nameof(CorrelationCaptureJob));
        await PublishJobToExchangeAsync(job, expectedCorrelationId, "test-consumer.correlationjob", cts.Token);

        await Task.WhenAny(executionFlag.Task, Task.Delay(TimeSpan.FromSeconds(15), cts.Token));

        await consumer.StopAsync(CancellationToken.None);

        // Assert
        executionFlag.Task.IsCompletedSuccessfully.Should().BeTrue();
        capturedCorrelationId.Should().Be(expectedCorrelationId, "CorrelationId should be parsed from headers");
    }

    [Fact]
    public async Task JobConsumer_ShouldGracefullyShutdown_WaitingForRunningJobs()
    {
        // Arrange
        await PurgeTestQueuesAsync();

        var jobStarted = new TaskCompletionSource<bool>();
        var jobCompleted = false;

        var services = BuildJobConsumerServiceProvider(sp =>
        {
            sp.AddTransient<IJobBase>(__ => new CallbackAsyncJob(async () =>
            {
                jobStarted.TrySetResult(true);
                await Task.Delay(2000); // Simulate work
                jobCompleted = true;
            }));
        });

        var sp = services.BuildServiceProvider();
        var workerOptions = CreateWorkerOptions();
        var consumer = new JobConsumer(
            sp,
            Microsoft.Extensions.Options.Options.Create(workerOptions),
            Microsoft.Extensions.Options.Options.Create(new JobConsumerOptions()),
            sp.GetRequiredService<IMilvaLogger>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var consumerTask = Task.Run(async () =>
        {
            try
            {
                await consumer.StartAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(2000, cts.Token);

        // Act - Publish a job then immediately request shutdown
        var correlationId = Guid.CreateVersion7();
        var job = CreateScheduledJob(nameof(CallbackAsyncJob));
        await PublishJobToExchangeAsync(job, correlationId, "test-consumer.callbackjob", cts.Token);

        // Wait for job to start
        await Task.WhenAny(jobStarted.Task, Task.Delay(TimeSpan.FromSeconds(10), cts.Token));
        jobStarted.Task.IsCompletedSuccessfully.Should().BeTrue("job should start before shutdown");

        // Request graceful shutdown while job is still running
        await cts.CancelAsync();

        // Wait for consumer to finish (should wait for running job)
        await Task.WhenAny(consumerTask, Task.Delay(TimeSpan.FromSeconds(15)));

        await consumer.StopAsync(CancellationToken.None);

        // Assert - Job should have completed before shutdown finished
        jobCompleted.Should().BeTrue("graceful shutdown should wait for running jobs to complete");
    }

    [Fact]
    public async Task JobConsumer_ShouldRetryFailedJob_WhenRetryCountBelowMax()
    {
        // Arrange
        await PurgeTestQueuesAsync();

        var executionCount = 0;
        var executionFlag = new TaskCompletionSource<bool>();

        var services = BuildJobConsumerServiceProvider(sp =>
        {
            sp.AddTransient<IJobBase>(__ => new CallbackAsyncJob(() =>
            {
                var count = Interlocked.Increment(ref executionCount);

                // First attempt fails, second succeeds
                if (count == 1)
                    throw new InvalidOperationException("Transient failure");

                executionFlag.TrySetResult(true);
            }));
        });

        var sp = services.BuildServiceProvider();
        var workerOptions = CreateWorkerOptions();
        var jobConsumerOptions = new JobConsumerOptions
        {
            [nameof(CallbackAsyncJob)] = new JobConsumerConfig
            {
                ExecutionTimeoutSeconds = 30,
                MaxRetries = 3,
                BaseRetryDelaySeconds = 1 // Short delay for test speed
            }
        };

        var consumer = new JobConsumer(
            sp,
            Microsoft.Extensions.Options.Options.Create(workerOptions),
            Microsoft.Extensions.Options.Options.Create(jobConsumerOptions),
            sp.GetRequiredService<IMilvaLogger>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        _ = Task.Run(async () =>
        {
            try
            {
                await consumer.StartAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(2000, cts.Token);

        // Act - Publish a job that will fail on first attempt
        var correlationId = Guid.CreateVersion7();
        var job = CreateScheduledJob(nameof(CallbackAsyncJob));
        await PublishJobToExchangeAsync(job, correlationId, "test-consumer.callbackjob", cts.Token);

        // Assert - Wait for retry to succeed
        var completed = await Task.WhenAny(executionFlag.Task, Task.Delay(TimeSpan.FromSeconds(20), cts.Token));

        await consumer.StopAsync(CancellationToken.None);

        executionFlag.Task.IsCompletedSuccessfully.Should().BeTrue("job should succeed on retry");
        executionCount.Should().Be(2, "job should be executed twice (1 failure + 1 success)");
    }

    [Fact]
    public async Task JobConsumer_ShouldMoveToDLQ_WhenMaxRetriesExceeded()
    {
        // Arrange
        await PurgeTestQueuesAsync();

        var executionCount = 0;

        var services = BuildJobConsumerServiceProvider(sp =>
        {
            sp.AddTransient<IJobBase>(__ => new CallbackAsyncJob(() =>
            {
                Interlocked.Increment(ref executionCount);
                throw new InvalidOperationException("Always fails");
            }));
        });

        var sp = services.BuildServiceProvider();
        var workerOptions = CreateWorkerOptions();
        var jobConsumerOptions = new JobConsumerOptions
        {
            [nameof(CallbackAsyncJob)] = new JobConsumerConfig
            {
                ExecutionTimeoutSeconds = 30,
                MaxRetries = 1,
                BaseRetryDelaySeconds = 1
            }
        };

        var consumer = new JobConsumer(
            sp,
            Microsoft.Extensions.Options.Options.Create(workerOptions),
            Microsoft.Extensions.Options.Options.Create(jobConsumerOptions),
            sp.GetRequiredService<IMilvaLogger>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        _ = Task.Run(async () =>
        {
            try
            {
                await consumer.StartAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(2000, cts.Token);

        // Act
        var correlationId = Guid.CreateVersion7();
        var job = CreateScheduledJob(nameof(CallbackAsyncJob));
        await PublishJobToExchangeAsync(job, correlationId, "test-consumer.callbackjob", cts.Token);

        // Wait for retries + DLQ routing
        await Task.Delay(8000, cts.Token);

        await consumer.StopAsync(CancellationToken.None);

        // Assert - Should have attempted initial + retry, then DLQ
        executionCount.Should().BeGreaterOrEqualTo(2, "should execute initial attempt + at least 1 retry");

        var rabbitFactory = new ConnectionFactory
        {
            HostName = GetRabbitMqHost(),
            Port = GetRabbitMqPort(),
            UserName = "guest",
            Password = "guest"
        };

        await using var connection = await rabbitFactory.CreateConnectionAsync(cts.Token);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cts.Token);

        var dlqResult = await channel.BasicGetAsync(WorkerConstant.Queues.FailedOccurrences, autoAck: true, cts.Token);
        dlqResult.Should().NotBeNull("job should end up in DLQ after max retries exceeded");
    }

    [Fact]
    public async Task JobConsumer_ShouldSkipRetryAndDLQ_WhenPermanentFailure()
    {
        // Arrange
        await PurgeTestQueuesAsync();

        var executionCount = 0;

        var services = BuildJobConsumerServiceProvider(sp =>
        {
            sp.AddTransient<IJobBase>(__ => new CallbackAsyncJob(() =>
            {
                Interlocked.Increment(ref executionCount);
                throw new Milvasoft.Milvaion.Sdk.Worker.Exceptions.PermanentJobException("Invalid data - no retry");
            }));
        });

        var sp = services.BuildServiceProvider();
        var workerOptions = CreateWorkerOptions();
        var jobConsumerOptions = new JobConsumerOptions
        {
            [nameof(CallbackAsyncJob)] = new JobConsumerConfig
            {
                ExecutionTimeoutSeconds = 30,
                MaxRetries = 5,
                BaseRetryDelaySeconds = 1
            }
        };

        var consumer = new JobConsumer(
            sp,
            Microsoft.Extensions.Options.Options.Create(workerOptions),
            Microsoft.Extensions.Options.Options.Create(jobConsumerOptions),
            sp.GetRequiredService<IMilvaLogger>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        _ = Task.Run(async () =>
        {
            try
            {
                await consumer.StartAsync(cts.Token);
            }
            catch (OperationCanceledException) { }
        }, cts.Token);

        await Task.Delay(2000, cts.Token);

        // Act
        var correlationId = Guid.CreateVersion7();
        var job = CreateScheduledJob(nameof(CallbackAsyncJob));
        await PublishJobToExchangeAsync(job, correlationId, "test-consumer.callbackjob", cts.Token);

        // Wait for processing
        await Task.Delay(3000, cts.Token);

        await consumer.StopAsync(CancellationToken.None);

        // Assert - Should execute only once (no retry for permanent failure)
        executionCount.Should().Be(1, "permanent failure should not be retried");

        var rabbitFactory = new ConnectionFactory
        {
            HostName = GetRabbitMqHost(),
            Port = GetRabbitMqPort(),
            UserName = "guest",
            Password = "guest"
        };

        await using var connection = await rabbitFactory.CreateConnectionAsync(cts.Token);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cts.Token);

        var dlqResult = await channel.BasicGetAsync(WorkerConstant.Queues.FailedOccurrences, autoAck: true, cts.Token);
        dlqResult.Should().NotBeNull("permanent failure should be routed directly to DLQ");
    }

    [Fact]
    public async Task JobConsumer_StopAsync_ShouldNotThrow_WhenNotStarted()
    {
        // Arrange
        var services = BuildJobConsumerServiceProvider(_ => { });
        var sp = services.BuildServiceProvider();
        var workerOptions = CreateWorkerOptions();

        var consumer = new JobConsumer(
            sp,
            Microsoft.Extensions.Options.Options.Create(workerOptions),
            Microsoft.Extensions.Options.Options.Create(new JobConsumerOptions()),
            sp.GetRequiredService<IMilvaLogger>());

        // Act & Assert - StopAsync without StartAsync should not throw
        var act = () => consumer.StopAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task WorkerJobTracker_ShouldNotGoNegative_WhenDecrementedBelowZero()
    {
        // Arrange
        var loggerFactory = GetLoggerFactory();
        var tracker = new WorkerJobTracker(loggerFactory);
        var workerId = "test-worker-negative";

        // Act - Decrement without any increment
        tracker.DecrementJobCount(workerId);

        // Assert
        tracker.GetJobCount(workerId).Should().BeGreaterOrEqualTo(0);
    }

    private ServiceCollection BuildJobConsumerServiceProvider(Action<ServiceCollection> configureJobs)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(GetLoggerFactory());
        services.AddScoped<IMilvaLogger>(sp => sp.GetRequiredService<ILoggerFactory>().CreateMilvaLogger<IMilvaLogger>());
        services.AddScoped<JobExecutor>();
        services.AddSingleton<WorkerJobTracker>();

        configureJobs(services);

        return services;
    }

    private async Task PurgeTestQueuesAsync(CancellationToken cancellationToken = default)
    {
        var rabbitFactory = new ConnectionFactory
        {
            HostName = GetRabbitMqHost(),
            Port = GetRabbitMqPort(),
            UserName = "guest",
            Password = "guest"
        };

        await using var connection = await rabbitFactory.CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        // Delete queues entirely so they can be recreated with correct arguments.
        // RabbitMQ does not allow re-declaring a queue with different arguments (e.g. DLX settings),
        // so purging alone is not sufficient.
        var queuesToDelete = new[]
        {
            $"{WorkerConstant.Queues.Jobs}.test-consumer.wildcard",
            WorkerConstant.Queues.FailedOccurrences,
            "test-dlq-source-queue"
        };

        foreach (var queue in queuesToDelete)
        {
            try
            {
                await channel.QueueDeleteAsync(queue, ifUnused: false, ifEmpty: false, cancellationToken: cancellationToken);
            }
            catch
            {
                // Queue might not exist yet
            }
        }
    }

    private async Task PublishJobToExchangeAsync(ScheduledJob job, Guid correlationId, string routingKey, CancellationToken cancellationToken)
    {
        var rabbitFactory = new ConnectionFactory
        {
            HostName = GetRabbitMqHost(),
            Port = GetRabbitMqPort(),
            UserName = "guest",
            Password = "guest"
        };

        await using var connection = await rabbitFactory.CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        await channel.ExchangeDeclareAsync(
            exchange: WorkerConstant.ExchangeName,
            type: "topic",
            durable: true,
            cancellationToken: cancellationToken);

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(job));
        var properties = new BasicProperties
        {
            Persistent = true,
            CorrelationId = correlationId.ToString(),
            Headers = new Dictionary<string, object>
            {
                { "CorrelationId", Encoding.UTF8.GetBytes(correlationId.ToString()) }
            }
        };

        await channel.BasicPublishAsync(
            exchange: WorkerConstant.ExchangeName,
            routingKey: routingKey,
            mandatory: false,
            basicProperties: properties,
            body: body,
            cancellationToken: cancellationToken);
    }

    #endregion

    #region Test Job Implementations

    private sealed class SuccessAsyncJob : IAsyncJob
    {
        public Task ExecuteAsync(IJobContext context)
        {
            context.Log("Job executed successfully");
            return Task.CompletedTask;
        }
    }

    private sealed class AnotherAsyncJob : IAsyncJob
    {
        public Task ExecuteAsync(IJobContext context) => Task.CompletedTask;
    }

    private sealed class FailingAsyncJob : IAsyncJob
    {
        public Task ExecuteAsync(IJobContext context) => throw new InvalidOperationException("Simulated job failure");
    }

    private sealed class SlowAsyncJob : IAsyncJob
    {
        public Task ExecuteAsync(IJobContext context) => Task.Delay(TimeSpan.FromSeconds(30), context.CancellationToken);
    }

    private sealed class PermanentFailureJob : IAsyncJob
    {
        public Task ExecuteAsync(IJobContext context) => throw new Milvasoft.Milvaion.Sdk.Worker.Exceptions.PermanentJobException("Invalid job data");
    }

    private sealed class CancellationAwareJob : IAsyncJob
    {
        public Task ExecuteAsync(IJobContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            return Task.Delay(TimeSpan.FromSeconds(60), context.CancellationToken);
        }
    }

    private sealed class AsyncJobWithResultImpl : IAsyncJobWithResult<string>
    {
        public Task<string> ExecuteAsync(IJobContext context) => Task.FromResult("Custom result from job");
    }

    private static readonly AsyncLocal<CancellationToken> CurrentJobToken = new();

    private sealed class CallbackAsyncJob(Func<Task> onExecute) : IAsyncJob
    {
        public CallbackAsyncJob(Action onExecute) : this(() =>
        {
            onExecute();
            return Task.CompletedTask;
        })
        { }

        public async Task ExecuteAsync(IJobContext context)
        {
            CurrentJobToken.Value = context.CancellationToken;

            await onExecute();
        }
    }

    private sealed class CorrelationCaptureJob(Action<Guid> onCapture) : IAsyncJob
    {
        public Task ExecuteAsync(IJobContext context)
        {
            onCapture(context.OccurrenceId);
            return Task.CompletedTask;
        }
    }

    #endregion
}
