using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Milvaion.Sdk.Worker.HealthChecks;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using Xunit.Abstractions;
using WorkerHealthCheckOptions = Milvasoft.Milvaion.Sdk.Worker.Options.HealthCheckOptions;

namespace Milvaion.IntegrationTests.WorkerSdk;

/// <summary>
/// Integration tests for FileHealthCheckBackgroundService.
/// Tests file-based health check output against file system.
/// </summary>
[Collection(nameof(WorkerSdkTestCollection))]
public class FileHealthCheckBackgroundServiceTests(WorkerSdkContainerFixture fixture, ITestOutputHelper output) : WorkerSdkTestBase(fixture, output)
{
    [Fact]
    public async Task FileHealthCheck_ShouldCreateLiveFile_WhenHealthy()
    {
        // Arrange

        var tempDir = Path.Combine(Path.GetTempPath(), $"healthcheck_{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(tempDir);

        var liveFile = Path.Combine(tempDir, "live");
        var readyFile = Path.Combine(tempDir, "ready");

        try
        {
            var options = new WorkerOptions
            {
                HealthCheck = new WorkerHealthCheckOptions
                {
                    Enabled = true,
                    LiveFilePath = liveFile,
                    ReadyFilePath = readyFile,
                    IntervalSeconds = 1
                }
            };

            var stubHealthCheckService = new StubHealthCheckService(HealthStatus.Healthy);
            var loggerFactory = new LoggerFactory();

            var service = new FileHealthCheckBackgroundService(stubHealthCheckService, options, loggerFactory);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            // Act
            var executeTask = service.StartAsync(cts.Token);
            await Task.Delay(1500, cts.Token);

            // Assert
            File.Exists(liveFile).Should().BeTrue("live file should be created when healthy");
            File.Exists(readyFile).Should().BeTrue("ready file should be created when healthy");

            var liveContent = await File.ReadAllTextAsync(liveFile);
            liveContent.Should().Be("ok");

            await cts.CancelAsync();

            try
            {
                await executeTask;
            }
            catch (OperationCanceledException) { }

            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task FileHealthCheck_ShouldRemoveReadyFile_WhenUnhealthy()
    {
        // Arrange

        var tempDir = Path.Combine(Path.GetTempPath(), $"healthcheck_{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(tempDir);

        var liveFile = Path.Combine(tempDir, "live");
        var readyFile = Path.Combine(tempDir, "ready");

        // Pre-create ready file (simulate previously healthy state)
        await File.WriteAllTextAsync(readyFile, "ok");

        try
        {
            var options = new WorkerOptions
            {
                HealthCheck = new WorkerHealthCheckOptions
                {
                    Enabled = true,
                    LiveFilePath = liveFile,
                    ReadyFilePath = readyFile,
                    IntervalSeconds = 1
                }
            };

            var stubHealthCheckService = new StubHealthCheckService(HealthStatus.Degraded);
            var loggerFactory = new LoggerFactory();

            var service = new FileHealthCheckBackgroundService(stubHealthCheckService, options, loggerFactory);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            // Act
            var executeTask = service.StartAsync(cts.Token);
            await Task.Delay(1500, cts.Token);

            // Assert - live file should exist (degraded is not unhealthy), but ready should not
            File.Exists(liveFile).Should().BeTrue("live file should exist for degraded status");
            File.Exists(readyFile).Should().BeFalse("ready file should be removed when not healthy");

            await cts.CancelAsync();

            try
            {
                await executeTask;
            }
            catch (OperationCanceledException) { }

            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task FileHealthCheck_ShouldCleanupFiles_OnStop()
    {
        // Arrange

        var tempDir = Path.Combine(Path.GetTempPath(), $"healthcheck_{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(tempDir);

        var liveFile = Path.Combine(tempDir, "live");
        var readyFile = Path.Combine(tempDir, "ready");

        // Pre-create files
        await File.WriteAllTextAsync(liveFile, "ok");
        await File.WriteAllTextAsync(readyFile, "ok");

        try
        {
            var options = new WorkerOptions
            {
                HealthCheck = new WorkerHealthCheckOptions
                {
                    Enabled = true,
                    LiveFilePath = liveFile,
                    ReadyFilePath = readyFile,
                    IntervalSeconds = 60 // Long interval so it doesn't run during test
                }
            };

            var stubHealthCheckService = new StubHealthCheckService(HealthStatus.Healthy);
            var loggerFactory = new LoggerFactory();

            var service = new FileHealthCheckBackgroundService(stubHealthCheckService, options, loggerFactory);

            // Act
            await service.StopAsync(CancellationToken.None);

            // Assert
            File.Exists(liveFile).Should().BeFalse("live file should be cleaned up on stop");
            File.Exists(readyFile).Should().BeFalse("ready file should be cleaned up on stop");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task FileHealthCheck_ShouldRemoveStaleFiles_WhenConstructed_EvenIfNeverStarted()
    {
        // Arrange - files left behind by a previous run of the worker (same container filesystem).
        var tempDir = Path.Combine(Path.GetTempPath(), $"healthcheck_{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(tempDir);

        var liveFile = Path.Combine(tempDir, "live");
        var readyFile = Path.Combine(tempDir, "ready");

        await File.WriteAllTextAsync(liveFile, "ok");
        await File.WriteAllTextAsync(readyFile, "ok");

        try
        {
            var options = new WorkerOptions
            {
                HealthCheck = new WorkerHealthCheckOptions
                {
                    Enabled = true,
                    LiveFilePath = liveFile,
                    ReadyFilePath = readyFile,
                    IntervalSeconds = 60
                }
            };

            // Act - the host constructs hosted services before starting them; if startup then fails
            // (e.g. RabbitMQ unreachable) this service is never started.
            _ = new FileHealthCheckBackgroundService(new StubHealthCheckService(HealthStatus.Healthy), options, new LoggerFactory());

            // Assert
            File.Exists(liveFile).Should().BeFalse("a worker that has not started must not report live from a stale file");
            File.Exists(readyFile).Should().BeFalse("a worker that has not started must not report ready from a stale file");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    #region Stubs

    private sealed class StubHealthCheckService(HealthStatus status) : HealthCheckService
    {
        public override Task<HealthReport> CheckHealthAsync(Func<HealthCheckRegistration, bool> predicate, CancellationToken cancellationToken = default)
        {
            var entries = new Dictionary<string, HealthReportEntry>
            {
                ["Stub"] = new HealthReportEntry(status, "Stub check", TimeSpan.FromMilliseconds(1), null, null)
            };

            return Task.FromResult(new HealthReport(entries, TimeSpan.FromMilliseconds(1)));
        }
    }

    #endregion
}
