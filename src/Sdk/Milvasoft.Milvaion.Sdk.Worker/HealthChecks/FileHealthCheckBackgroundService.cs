using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Utils;
using Milvasoft.Milvaion.Sdk.Worker.Options;

namespace Milvasoft.Milvaion.Sdk.Worker.HealthChecks;

/// <summary>
/// Background service that performs periodic health checks using <see cref="HealthCheckService"/> and writes status to file.
/// Optimized for minimal performance impact on job execution.
/// </summary>
public sealed class FileHealthCheckBackgroundService : BackgroundService
{
    private readonly HealthCheckService _healthCheckService;
    private readonly IMilvaLogger _logger;
    private readonly string _liveFilePath;
    private readonly string _readyFilePath;
    private readonly TimeSpan _checkInterval;

    /// <summary>
    /// Creates the service and removes health files left over from a previous run of the process.
    /// Hosted services are constructed before any of them starts, so a worker whose startup fails
    /// (e.g. RabbitMQ unreachable) never reports a stale "live"/"ready" status.
    /// </summary>
    public FileHealthCheckBackgroundService(HealthCheckService healthCheckService, WorkerOptions options, ILoggerFactory loggerFactory)
    {
        _healthCheckService = healthCheckService;
        _logger = loggerFactory.CreateMilvaLogger<FileHealthCheckBackgroundService>();
        _liveFilePath = options.HealthCheck.LiveFilePath;
        _readyFilePath = options.HealthCheck.ReadyFilePath;
        _checkInterval = TimeSpan.FromSeconds(options.HealthCheck.IntervalSeconds);

        CleanupHealthFile();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information("Health check service started. Interval: {Interval}s, Files: {Path}", _checkInterval.TotalSeconds, _liveFilePath + " - " + _readyFilePath);

        // Initial check
        await PerformHealthCheckAsync(stoppingToken);

        using var timer = new PeriodicTimer(_checkInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
                await PerformHealthCheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Health check iteration failed");
            }
        }

        // Cleanup on shutdown
        CleanupHealthFile();
    }

    private async Task PerformHealthCheckAsync(CancellationToken cancellationToken)
    {
        var report = await _healthCheckService.CheckHealthAsync(cancellationToken);

        var live = report.Status != HealthStatus.Unhealthy;
        var ready = report.Status == HealthStatus.Healthy;

        SetFile(_liveFilePath, live);
        SetFile(_readyFilePath, ready);

        if (!ready)
        {
            _logger.Warning("Worker not ready. Status: {Status}", report.Status);

            foreach (var entry in report.Entries.Where(e => e.Value.Status != HealthStatus.Healthy))
                _logger.Warning("Unhealthy check: {Name} - {Status} - {Description}", entry.Key, entry.Value.Status, entry.Value.Description);
        }
    }

    private void CleanupHealthFile()
    {
        try
        {
            if (File.Exists(_liveFilePath))
                File.Delete(_liveFilePath);

            if (File.Exists(_readyFilePath))
                File.Delete(_readyFilePath);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to cleanup health files");
        }
    }
    private static void SetFile(string path, bool exists)
    {
        if (exists)
        {
            File.WriteAllText(path, "ok");
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.Information("Health check service stopping");

        CleanupHealthFile();

        await base.StopAsync(cancellationToken);
    }
}