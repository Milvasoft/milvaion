using Microsoft.Extensions.Logging;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Utils;
using System.Collections.Concurrent;

namespace Milvasoft.Milvaion.Sdk.Worker.Core;

/// <summary>
/// Tracks current job counts for each worker in real-time.
/// Thread-safe singleton service.
/// </summary>
public class WorkerJobTracker(ILoggerFactory loggerFactory)
{
    private readonly ConcurrentDictionary<string, int> _currentJobs = new();
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<WorkerJobTracker>();

    /// <summary>
    /// Increments the job count for a worker.
    /// </summary>
    /// <param name="workerId">Worker identifier</param>
    public void IncrementJobCount(string workerId)
    {
        var newCount = _currentJobs.AddOrUpdate(workerId, 1, (_, count) => count + 1);

        _logger.Debug($"[JobTracker] IncrementJobCount({workerId}) -> {newCount} (ProcessId: {Environment.ProcessId})");
    }

    /// <summary>
    /// Decrements the job count for a worker.
    /// </summary>
    /// <param name="workerId">Worker identifier</param>
    public void DecrementJobCount(string workerId)
    {
        var newCount = _currentJobs.AddOrUpdate(workerId, 0, (_, count) => Math.Max(0, count - 1));

        _logger.Debug($"[JobTracker] DecrementJobCount({workerId}) -> {newCount} (ProcessId: {Environment.ProcessId})");
    }

    /// <summary>
    /// Gets the current job count for a worker.
    /// </summary>
    /// <param name="workerId">Worker identifier</param>
    /// <returns>Current number of jobs being processed by the worker</returns>
    public int GetJobCount(string workerId) => _currentJobs.TryGetValue(workerId, out var count) ? count : 0;

    /// <summary>
    /// Gets all worker job counts.
    /// </summary>
    /// <returns>Dictionary of workerId to job count</returns>
    public Dictionary<string, int> GetAllJobCounts() => new(_currentJobs);
}
