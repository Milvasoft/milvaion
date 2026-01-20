using Milvaion.Application.Dtos.AdminDtos;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Milvaion.Infrastructure.BackgroundServices.Base;

/// <summary>
/// Registry for collecting memory stats from all tracked background services.
/// </summary>
public interface IMemoryStatsRegistry
{
    /// <summary>
    /// Registers a memory stats provider for a service.
    /// </summary>
    /// <param name="serviceName"></param>
    /// <param name="statsProvider"></param>
    void Register(string serviceName, Func<MemoryTrackStats> statsProvider);

    /// <summary>
    /// Unregisters a memory stats provider for a service.
    /// </summary>
    /// <param name="serviceName"></param>
    void Unregister(string serviceName);

    /// <summary>
    /// Gets memory stats for a specific service.
    /// </summary>
    /// <param name="serviceName"></param>
    /// <returns></returns>
    MemoryTrackStats GetStats(string serviceName);

    /// <summary>
    /// Gets aggregated memory stats across all registered services.
    /// </summary>
    /// <returns></returns>
    AggregatedMemoryStats GetAggregatedStats();
}

/// <summary>
/// Default implementation of memory stats registry.
/// </summary>
public class MemoryStatsRegistry : IMemoryStatsRegistry
{
    private readonly ConcurrentDictionary<string, Func<MemoryTrackStats>> _statsProviders = new();

    /// <inheritdoc/>
    public void Register(string serviceName, Func<MemoryTrackStats> statsProvider) => _statsProviders[serviceName] = statsProvider;

    /// <inheritdoc/>
    public void Unregister(string serviceName) => _statsProviders.TryRemove(serviceName, out _);

    /// <inheritdoc/>
    public MemoryTrackStats GetStats(string serviceName) => _statsProviders.TryGetValue(serviceName, out var provider) ? provider() : null;

    /// <inheritdoc/>
    public AggregatedMemoryStats GetAggregatedStats()
    {
        var serviceStats = _statsProviders.Values.Select(provider => provider()).ToList();

        using var process = Process.GetCurrentProcess();

        return new AggregatedMemoryStats
        {
            Timestamp = DateTime.UtcNow,
            TotalManagedMemoryBytes = GC.GetTotalMemory(forceFullCollection: false),
            TotalProcessMemoryBytes = process.WorkingSet64,
            RunningServicesCount = serviceStats.Count(s => s.IsRunning),
            ServicesWithPotentialLeaks = serviceStats.Count(s => s.PotentialMemoryLeak),
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2),
            ServiceStats = serviceStats
        };
    }
}