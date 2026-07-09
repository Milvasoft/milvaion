using Microsoft.Extensions.Logging;
using Milvasoft.Milvaion.Sdk.Models;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Milvasoft.Milvaion.Sdk.Worker.Core;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using System.Text.Json;

namespace Milvasoft.Milvaion.Sdk.Worker.Testing;

/// <summary>
/// Fluent builder for running Milvaion job implementations in local tests.
/// No RabbitMQ, Redis, or database required.
/// <para>
/// Usage:
/// <code>
/// var result = await JobTestRunner
///     .For(new MySendEmailJob())
///     .WithJobData(new EmailJobData { To = "user@example.com", Subject = "Hello" })
///     .WithTimeout(30)
///     .RunAsync();
///
/// result.Status.Should().Be(JobOccurrenceStatus.Completed);
/// </code>
/// </para>
/// </summary>
public sealed class JobTestRunner
{
    private readonly IJobBase _job;
    private string _jobData;
    private int _timeoutSeconds = 30;
    private string _workerId = "local-test";
    private CancellationToken _cancellationToken = CancellationToken.None;
    private ILoggerFactory _loggerFactory;

    private JobTestRunner(IJobBase job) => _job = job ?? throw new ArgumentNullException(nameof(job));

    /// <summary>
    /// Creates a test runner for the given job instance.
    /// </summary>
    public static JobTestRunner For(IJobBase job) => new(job);

    /// <summary>
    /// Sets the typed job data. Serialized to JSON automatically.
    /// </summary>
    public JobTestRunner WithJobData<TData>(TData data) where TData : class
    {
        _jobData = JsonSerializer.Serialize(data);
        return this;
    }

    /// <summary>
    /// Sets raw JSON job data string.
    /// </summary>
    public JobTestRunner WithJobData(string json)
    {
        _jobData = json;
        return this;
    }

    /// <summary>
    /// Overrides the execution timeout in seconds. Default: 30.
    /// Set to 0 to disable timeout.
    /// </summary>
    public JobTestRunner WithTimeout(int seconds)
    {
        _timeoutSeconds = seconds;
        return this;
    }

    /// <summary>
    /// Overrides the worker ID used in the test context. Default: "local-test".
    /// </summary>
    public JobTestRunner WithWorkerId(string workerId)
    {
        _workerId = workerId;
        return this;
    }

    /// <summary>
    /// Supplies a cancellation token (e.g., for cancellation testing).
    /// </summary>
    public JobTestRunner WithCancellationToken(CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        return this;
    }

    /// <summary>
    /// Supplies a custom <see cref="ILoggerFactory"/>.
    /// Useful for capturing log output in tests (e.g., xUnit's ITestOutputHelper wrapped in a logger).
    /// If not provided, defaults to a console logger.
    /// </summary>
    public JobTestRunner WithLoggerFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        return this;
    }

    /// <summary>
    /// Executes the job and returns the <see cref="JobExecutionResult"/>.
    /// The result carries Status, DurationMs, Exception, Result, and log entries.
    /// </summary>
    public async Task<JobExecutionResult> RunAsync()
    {
        var loggerFactory = _loggerFactory ?? LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));

        var executor = new JobExecutor(loggerFactory);

        var jobName = _job.GetType().Name;

        var scheduledJob = new ScheduledJob
        {
            Id = Guid.CreateVersion7(),
            JobNameInWorker = jobName,
            DisplayName = jobName,
            Description = $"Local test run for {jobName}",
            IsActive = true,
            ExecuteAt = DateTime.UtcNow,
            JobData = _jobData ?? "{}"
        };

        var workerOptions = new WorkerOptions { WorkerId = _workerId };
        workerOptions.RegenerateInstanceId();

        var consumerConfig = new JobConsumerConfig
        {
            ConsumerId = $"{jobName.ToLowerInvariant()}-local-test",
            ExecutionTimeoutSeconds = _timeoutSeconds
        };

        return await executor.ExecuteAsync(_job,
                                           scheduledJob,
                                           Guid.CreateVersion7(),
                                           null,
                                           workerOptions,
                                           consumerConfig,
                                           _cancellationToken);
    }
}
