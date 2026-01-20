using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Milvaion.Application.Dtos.AdminDtos;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Utils;
using System.Diagnostics;

namespace Milvaion.Infrastructure.BackgroundServices.Base;

/// <summary>
/// Base class for background services with memory tracking capabilities.
/// Monitors memory usage, detects memory leaks, and logs metrics.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="MemoryTrackedBackgroundService"/> class.
/// </remarks>
/// <param name="loggerFactory">Logger factory</param>
/// <param name="options">Memory tracking configuration (optional)</param>
/// <param name="memoryStatsRegistry">Optional registry for exposing stats via endpoint</param>
public abstract class MemoryTrackedBackgroundService(ILoggerFactory loggerFactory, BackgrounServiceOptions options, IMemoryStatsRegistry memoryStatsRegistry = null) : BackgroundService
{
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<MemoryTrackedBackgroundService>();
    private readonly MemoryTrackingOptions _options = options.MemoryTrackingOptions ?? new MemoryTrackingOptions();
    private readonly IMemoryStatsRegistry _memoryStatsRegistry = memoryStatsRegistry;
    private long _lastMemoryUsed;
    private long _startMemory;
    private DateTime _startTime;
    private DateTime _lastMemoryCheckTime = DateTime.UtcNow;
    private bool _potentialMemoryLeak;

    /// <summary>
    /// Determines if the service is currently running.
    /// </summary>
    protected bool _isRunning;

    /// <summary>
    /// Service name for logging (override in derived classes).
    /// </summary>
    protected abstract string ServiceName { get; }

    /// <summary>
    /// Gets the current memory tracking statistics for this service.
    /// </summary>
    public MemoryTrackStats GetStats()
    {
        var currentMemory = GetCurrentMemoryUsage();

        return new MemoryTrackStats
        {
            ServiceName = ServiceName,
            InitialMemoryBytes = _startMemory,
            CurrentMemoryBytes = currentMemory,
            LastMemoryBytes = _lastMemoryUsed,
            TotalGrowthBytes = currentMemory - _startMemory,
            ProcessMemoryBytes = GetProcessMemoryUsage(),
            StartTime = _startTime,
            LastCheckTime = _lastMemoryCheckTime,
            PotentialMemoryLeak = _potentialMemoryLeak,
            IsRunning = _isRunning,
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2)
        };
    }

    /// <summary>
    /// Execute implementation with memory tracking.
    /// </summary>
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _startTime = DateTime.UtcNow;
        _startMemory = GetCurrentMemoryUsage();
        _lastMemoryUsed = _startMemory;
        _isRunning = true;

        // Register with stats registry if available
        _memoryStatsRegistry?.Register(ServiceName, GetStats);

        _logger.Information("[{ServiceName}] Started. Initial Memory: {MemoryMB} MB, Process Memory: {ProcessMB} MB", ServiceName, _startMemory / 1024 / 1024, GetProcessMemoryUsage() / 1024 / 1024);

        try
        {
            await ExecuteWithMemoryTrackingAsync(stoppingToken);
        }
        finally
        {
            _isRunning = false;
            LogFinalMemoryStats();
            _memoryStatsRegistry?.Unregister(ServiceName);
        }
    }

    /// <summary>
    /// Override this method instead of ExecuteAsync in derived classes.
    /// </summary>
    protected abstract Task ExecuteWithMemoryTrackingAsync(CancellationToken stoppingToken);

    /// <summary>
    /// Tracks memory after each iteration. Call this at the end of each work cycle.
    /// </summary>
    protected void TrackMemoryAfterIteration()
    {
        // Only check memory at configured intervals
        var timeSinceLastCheck = DateTime.UtcNow - _lastMemoryCheckTime;

        if (timeSinceLastCheck.TotalSeconds < _options.CheckIntervalSeconds)
            return;

        var currentMemory = GetCurrentMemoryUsage();
        var memoryGrowth = currentMemory - _lastMemoryUsed;
        var totalGrowth = currentMemory - _startMemory;
        var processMemory = GetProcessMemoryUsage();

        // Log periodic stats (every check interval)
        _logger.Debug("[{ServiceName}] Memory Stats - Current: {CurrentMB} MB, Growth: {GrowthMB:+0;-0} MB, Total Growth: {TotalGrowthMB:+0;-0} MB, Process: {ProcessMB} MB", ServiceName, currentMemory / 1024 / 1024, memoryGrowth / 1024.0 / 1024, totalGrowth / 1024.0 / 1024, processMemory / 1024 / 1024);

        // Detect memory growth spikes
        if (memoryGrowth > _options.WarningThresholdBytes)
        {
            _logger.Warning("[{ServiceName}] Memory growth detected: +{GrowthMB} MB in {Seconds}s", ServiceName, memoryGrowth / 1024.0 / 1024, timeSinceLastCheck.TotalSeconds);

            // Suggest GC if growth is significant
            if (memoryGrowth > _options.CriticalThresholdBytes)
            {
                _logger.Error("[{ServiceName}] CRITICAL memory growth: +{GrowthMB} MB. Forcing GC...", ServiceName, memoryGrowth / 1024.0 / 1024);

                ForceGarbageCollection();
            }
        }

        // Detect memory leaks (continuous growth over time)
        var uptimeHours = (DateTime.UtcNow - _startTime).TotalHours;
        if (totalGrowth > _options.LeakDetectionThresholdBytes && uptimeHours > 1)
        {
            _potentialMemoryLeak = true;
            _logger.Error("[{ServiceName}] Potential MEMORY LEAK detected! Total growth: {TotalGrowthMB} MB over {Hours:F1} hours", ServiceName, totalGrowth / 1024.0 / 1024, uptimeHours);
        }

        _lastMemoryUsed = currentMemory;
        _lastMemoryCheckTime = DateTime.UtcNow;
    }

    /// <summary>
    /// Forces garbage collection and logs results.
    /// </summary>
    protected void ForceGarbageCollection()
    {
        var beforeGC = GetCurrentMemoryUsage();

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var afterGC = GetCurrentMemoryUsage();
        var freedMemory = beforeGC - afterGC;

        _logger.Information("[{ServiceName}] GC completed. Before: {BeforeMB} MB, After: {AfterMB} MB, Freed: {FreedMB} MB", ServiceName, beforeGC / 1024 / 1024, afterGC / 1024 / 1024, freedMemory / 1024.0 / 1024);

        // Update baseline after GC
        _lastMemoryUsed = afterGC;
    }

    /// <summary>
    /// Gets current managed memory usage.
    /// </summary>
    private static long GetCurrentMemoryUsage() => GC.GetTotalMemory(forceFullCollection: false);

    /// <summary>
    /// Gets current process memory usage (includes native memory).
    /// </summary>
    private static long GetProcessMemoryUsage()
    {
        using var process = Process.GetCurrentProcess();

        // Physical memory usage
        return process.WorkingSet64;
    }

    /// <summary>
    /// Logs final memory statistics when service stops.
    /// </summary>
    private void LogFinalMemoryStats()
    {
        var finalMemory = GetCurrentMemoryUsage();
        var totalGrowth = finalMemory - _startMemory;
        var processMemory = GetProcessMemoryUsage();
        var uptime = DateTime.UtcNow - _startTime;

        _logger.Information("[{ServiceName}] Stopped. Final Memory: {FinalMB} MB, Total Growth: {GrowthMB:+0;-0} MB, Process Memory: {ProcessMB} MB, Uptime: {Uptime}", ServiceName, finalMemory / 1024 / 1024, totalGrowth / 1024.0 / 1024, processMemory / 1024 / 1024, uptime);

        // Log GC statistics
        LogGCStatistics();
    }

    /// <summary>
    /// Logs garbage collection statistics.
    /// </summary>
    private void LogGCStatistics()
    {
        var gen0Collections = GC.CollectionCount(0);
        var gen1Collections = GC.CollectionCount(1);
        var gen2Collections = GC.CollectionCount(2);

        _logger.Information("[{ServiceName}] GC Statistics - Gen0: {Gen0}, Gen1: {Gen1}, Gen2: {Gen2}", ServiceName, gen0Collections, gen1Collections, gen2Collections);
    }
}

