using Microsoft.Extensions.Logging;
using Milvaion.Application.Interfaces.Redis;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Helpers;
using Milvasoft.Milvaion.Sdk.Models;
using Milvasoft.Milvaion.Sdk.Utils;
using StackExchange.Redis;
using System.Text.Json;

namespace Milvaion.Infrastructure.Services.Redis;

/// <summary>
/// Redis-based worker tracking implementation.
/// Uses Redis Hashes with TTL for automatic zombie detection.
/// Returns CachedWorker models (not DB entities).
/// </summary>
public class RedisWorkerService(IConnectionMultiplexer redis,
                                IRedisCircuitBreaker circuitBreaker,
                                ILoggerFactory loggerFactory) : IRedisWorkerService
{
    private readonly IConnectionMultiplexer _redis = redis;
    private readonly IRedisCircuitBreaker _circuitBreaker = circuitBreaker;
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<RedisWorkerService>();
    private readonly IDatabase _db = redis.GetDatabase();
    private readonly TimeSpan _instanceTTL = TimeSpan.FromMinutes(2); // Auto-expire zombie instances
    private readonly TimeSpan _workerMetadataTTL = TimeSpan.FromMinutes(5); // Worker metadata expires if no active instances

    /// <inheritdoc/>
    public Task<bool> RegisterWorkerAsync(WorkerDiscoveryRequest registration, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                var workerKey = $"workers:{registration.WorkerId}";
                var instanceKey = $"workers:{registration.WorkerId}:instances:{registration.InstanceId}";

                // Store worker metadata (static config)
                var workerData = new Dictionary<string, RedisValue>
                {
                    ["displayName"] = registration.DisplayName,
                    ["routingPatterns"] = JsonSerializer.Serialize(registration.RoutingPatterns),
                    ["jobDataDefinitions"] = JsonSerializer.Serialize(registration.JobDataDefinitions),
                    ["jobNames"] = JsonSerializer.Serialize(registration.JobTypes),
                    ["maxParallelJobs"] = registration.MaxParallelJobs,
                    ["version"] = registration.Version,
                    ["metadata"] = registration.Metadata,
                    ["registeredAt"] = DateTime.UtcNow.ToString("O")
                };

                await _db.HashSetAsync(workerKey, [.. workerData.Select(kvp => new HashEntry(kvp.Key, kvp.Value))]);

                // Add TTL to worker metadata
                await _db.KeyExpireAsync(workerKey, _workerMetadataTTL);

                // Store instance data with TTL (auto-expire)
                var instanceData = new Dictionary<string, RedisValue>
                {
                    ["hostName"] = registration.HostName,
                    ["ipAddress"] = registration.IpAddress,
                    ["currentJobs"] = 0,
                    ["status"] = WorkerStatus.Active.ToString(),
                    ["lastHeartbeat"] = DateTime.UtcNow.ToString("O"),
                    ["registeredAt"] = DateTime.UtcNow.ToString("O")
                };

                await _db.HashSetAsync(instanceKey, [.. instanceData.Select(kvp => new HashEntry(kvp.Key, kvp.Value))]);

                // Auto-zombie detection
                await _db.KeyExpireAsync(instanceKey, _instanceTTL);

                return true;
            },
            fallback: async () => false,
            operationName: "RegisterWorker",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<bool> UpdateHeartbeatAsync(string workerId, string instanceId, int currentJobs, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                var instanceKey = $"workers:{workerId}:instances:{instanceId}";
                var workerKey = $"workers:{workerId}";

                // Check if instance exists
                if (!await _db.KeyExistsAsync(instanceKey))
                {
                    _logger.Debug($"[REDIS] UpdateHeartbeat FAILED: Instance key '{instanceKey}' does not exist");
                    return false;
                }

                // Update heartbeat and current jobs
                await _db.HashSetAsync(instanceKey,
                [
                    new HashEntry("lastHeartbeat", DateTime.UtcNow.ToString("O")),
                    new HashEntry("currentJobs", currentJobs),
                    new HashEntry("status", WorkerStatus.Active.ToString())
                ]);

                // Refresh TTL on both instance and worker metadata (keep alive while instances are active)
                await _db.KeyExpireAsync(instanceKey, _instanceTTL);
                await _db.KeyExpireAsync(workerKey, _workerMetadataTTL);

                _logger.Debug($"[REDIS] UpdateHeartbeat SUCCESS: {instanceKey} -> currentJobs={currentJobs}");

                return true;
            },
            fallback: async () => false,
            operationName: "UpdateHeartbeat",
            cancellationToken: cancellationToken
        );

    /// <summary>
    /// Updates heartbeats for multiple worker instances in a single pipeline operation.
    /// Significantly faster than calling UpdateHeartbeatAsync multiple times.
    /// </summary>
    /// <param name="updates">List of worker heartbeat updates (WorkerId, InstanceId, CurrentJobs)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of successfully updated instances</returns>
    public async Task<int> BulkUpdateHeartbeatsAsync(List<(string WorkerId, string InstanceId, int CurrentJobs)> updates, CancellationToken cancellationToken = default)
    {
        if (updates == null || updates.Count == 0)
            return 0;

        try
        {
            var now = DateTime.UtcNow.ToString("O");
            var successCount = 0;

            // PIPELINE: Fire all updates in parallel
            var updateTasks = updates.Select(async update =>
            {
                var instanceKey = $"workers:{update.WorkerId}:instances:{update.InstanceId}";
                var workerKey = $"workers:{update.WorkerId}";

                // Check existence first (fast operation)
                if (!await _db.KeyExistsAsync(instanceKey))
                    return false;

                // Update data
                await _db.HashSetAsync(instanceKey,
                [
                    new HashEntry("lastHeartbeat", now),
                    new HashEntry("currentJobs", update.CurrentJobs),
                    new HashEntry("status", WorkerStatus.Active.ToString())
                ]);

                // Refresh TTL
                await _db.KeyExpireAsync(instanceKey, _instanceTTL);
                await _db.KeyExpireAsync(workerKey, _workerMetadataTTL);

                return true;
            }).ToList();

            var results = await Task.WhenAll(updateTasks);
            successCount = results.Count(r => r);

            _logger.Debug($"[REDIS] BulkUpdateHeartbeats: {successCount}/{updates.Count} instances updated via pipeline");

            return successCount;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to bulk update heartbeats");
            return 0;
        }
    }

    /// <inheritdoc/>
    public Task<CachedWorker> GetWorkerAsync(string workerId, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                var workerKey = $"workers:{workerId}";

                // Check if worker metadata exists
                if (!await _db.KeyExistsAsync(workerKey))
                    return null;

                // Get worker metadata
                var workerData = await _db.HashGetAllAsync(workerKey);

                if (workerData.Length == 0)
                    return null;

                var workerDict = workerData.ToDictionary(x => x.Name.ToString(), x => x.Value.ToString());

                // Get all instances
                var instancesPattern = $"workers:{workerId}:instances:*";
                var server = _redis.GetServer(_redis.GetEndPoints().First());
                var instanceKeys = server.Keys(pattern: instancesPattern).ToList();

                if (instanceKeys.Count == 0)
                {
                    _logger.Warning("[GetWorkerAsync] Worker {WorkerId} has metadata but no active instances. Removing stale metadata.", workerId);

                    try
                    {
                        await _db.KeyDeleteAsync(workerKey);
                        _logger.Information("Removed stale worker metadata for {WorkerId}", workerId);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Failed to remove stale metadata for {WorkerId}", workerId);
                    }

                    return null;
                }

                // PIPELINE: Fetch all instance data in parallel
                var instanceDataTasks = instanceKeys.Select(key =>
                    _db.HashGetAllAsync(key)
                ).ToList();

                var instanceDataResults = await Task.WhenAll(instanceDataTasks);

                var instances = new List<WorkerInstance>();

                for (int i = 0; i < instanceKeys.Count; i++)
                {
                    var instanceData = instanceDataResults[i];
                    var instanceKey = instanceKeys[i];

                    if (instanceData.Length == 0)
                    {
                        _logger.Debug("[GetWorkerAsync] Instance key exists but data is empty (likely expired during read): {InstanceKey}", instanceKey);
                        continue;
                    }

                    var instanceDict = instanceData.ToDictionary(x => x.Name.ToString(), x => x.Value.ToString());

                    var instanceId = instanceKey.ToString().Split(':').Last();

                    var instance = new WorkerInstance
                    {
                        InstanceId = instanceId,
                        HostName = instanceDict.GetValueOrDefault("hostName"),
                        IpAddress = instanceDict.GetValueOrDefault("ipAddress"),
                        CurrentJobs = int.Parse(instanceDict.GetValueOrDefault("currentJobs", "0")),
                        Status = Enum.Parse<WorkerStatus>(instanceDict.GetValueOrDefault("status", "Active")),
                        LastHeartbeat = DateTime.Parse(instanceDict.GetValueOrDefault("lastHeartbeat", DateTime.UtcNow.ToString("O"))),
                        RegisteredAt = DateTime.Parse(instanceDict.GetValueOrDefault("registeredAt", DateTime.UtcNow.ToString("O")))
                    };

                    instances.Add(instance);
                }

                // ⚠️ FIX: If no active instances found after pipeline fetch, remove stale metadata and return null
                if (instances.Count == 0)
                {
                    _logger.Warning("[GetWorkerAsync] Worker {WorkerId} metadata exists but all instances expired. Removing stale metadata.", workerId);

                    try
                    {
                        await _db.KeyDeleteAsync(workerKey);
                        _logger.Information("Removed stale worker metadata for {WorkerId}", workerId);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Failed to remove stale metadata for {WorkerId}", workerId);
                    }

                    return null;
                }

                // Return CachedWorker (not MilvaionWorker entity)
                var cachedWorker = new CachedWorker
                {
                    WorkerId = workerId,
                    DisplayName = workerDict.GetValueOrDefault("displayName"),
                    RoutingPatterns = DeserializeDictionaryOrDefault(workerDict.GetValueOrDefault("routingPatterns")),
                    JobDataDefinitions = DeserializeDictionaryOrDefault(workerDict.GetValueOrDefault("jobDataDefinitions")),
                    JobNames = JsonSerializer.Deserialize<List<string>>(workerDict.GetValueOrDefault("jobNames", "[]")),
                    MaxParallelJobs = int.Parse(workerDict.GetValueOrDefault("maxParallelJobs", "0")),
                    Version = workerDict.GetValueOrDefault("version"),
                    Metadata = workerDict.GetValueOrDefault("metadata"),
                    RegisteredAt = DateTime.Parse(workerDict.GetValueOrDefault("registeredAt", DateTime.UtcNow.ToString("O"))),
                    Status = instances.Any(i => i.Status == WorkerStatus.Active) ? WorkerStatus.Active : WorkerStatus.Zombie,
                    LastHeartbeat = !instances.IsNullOrEmpty() ? instances.Max(i => i.LastHeartbeat) : null,
                    CurrentJobs = instances.Sum(i => i.CurrentJobs),
                    Instances = instances
                };

                return cachedWorker;
            },
            fallback: async () => null,
            operationName: "GetWorker",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<List<CachedWorker>> GetAllWorkersAsync(CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                var server = _redis.GetServer(_redis.GetEndPoints().First());

                var workerKeys = server.Keys(pattern: "workers:*").Where(k => !k.ToString().Contains(":instances:")).ToList();

                if (workerKeys.Count == 0)
                    return [];

                // PIPELINE: Fetch all worker metadata in parallel
                var workerDataTasks = workerKeys.Select(async workerKey =>
                {
                    var workerId = workerKey.ToString().Replace("workers:", "");
                    var worker = await GetWorkerAsync(workerId, cancellationToken);
                    return worker;
                }).ToList();

                var workers = await Task.WhenAll(workerDataTasks);

                // Filter out nulls (expired/invalid workers)
                return [.. workers.Where(w => w != null)];
            },
            fallback: async () => new List<CachedWorker>(),
            operationName: "GetAllWorkers",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<bool> IsWorkerActiveAsync(string workerId, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                var worker = await GetWorkerAsync(workerId, cancellationToken);

                return worker?.Status == WorkerStatus.Active;
            },
            fallback: async () => false,
            operationName: "IsWorkerActive",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public async Task<(int CurrentJobs, int? MaxParallelJobs)> GetWorkerCapacityAsync(string workerId, CancellationToken cancellationToken = default) => await _circuitBreaker.ExecuteAsync<(int, int?)>(
            operation: async () =>
            {
                try
                {
                    var worker = await GetWorkerAsync(workerId, cancellationToken);

                    if (worker == null)
                        return (0, null);

                    var currentJobs = worker.Instances?.Sum(i => i.CurrentJobs) ?? 0;

                    return (currentJobs, worker.MaxParallelJobs);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to get worker capacity for {WorkerId}", workerId);
                    return (0, null);
                }
            },
            fallback: async () => (0, (int?)null),
            operationName: "GetWorkerCapacity",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public async Task<(int CurrentJobs, int? MaxParallelJobs)> GetConsumerCapacityAsync(string workerId, string jobType, CancellationToken cancellationToken = default) => await _circuitBreaker.ExecuteAsync<(int, int?)>(
            operation: async () =>
            {
                try
                {
                    var worker = await GetWorkerAsync(workerId, cancellationToken);

                    if (worker == null)
                        return (0, (int?)null);

                    // Get consumer max parallel jobs from metadata
                    int? consumerMaxParallel = null;

                    if (!string.IsNullOrEmpty(worker.Metadata))
                    {
                        try
                        {
                            var metadata = JsonSerializer.Deserialize<WorkerMetadata>(worker.Metadata);
                            var consumerConfig = metadata?.JobConfigs?.FirstOrDefault(c => c.JobType == jobType);

                            if (consumerConfig != null)
                                consumerMaxParallel = consumerConfig.MaxParallelJobs;
                        }
                        catch
                        {
                            // Metadata parse failed, ignore
                        }
                    }

                    // Get current running jobs for this consumer from Redis counter
                    var consumerKey = $"consumer:{workerId}:{jobType}:count";
                    var currentJobsStr = await _db.StringGetAsync(consumerKey);
                    var currentJobs = currentJobsStr.HasValue ? (int)currentJobsStr : 0;

                    return (currentJobs, consumerMaxParallel);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to get consumer capacity for {WorkerId}/{JobType}", workerId, jobType);
                    return (0, (int?)null);
                }
            },
            fallback: async () => (0, (int?)null),
            operationName: "GetConsumerCapacity",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task IncrementConsumerJobCountAsync(string workerId, string jobType, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    var consumerKey = $"consumer:{workerId}:{jobType}:count";
                    await _db.StringIncrementAsync(consumerKey);
                    await _db.KeyExpireAsync(consumerKey, TimeSpan.FromHours(1)); // Auto-cleanup
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to increment consumer job count for {WorkerId}/{JobType}", workerId, jobType);
                    throw;
                }
            },
            fallback: async () => true,
            operationName: "IncrementConsumerJobCount",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task DecrementConsumerJobCountAsync(string workerId, string jobType, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    var consumerKey = $"consumer:{workerId}:{jobType}:count";
                    var newValue = await _db.StringDecrementAsync(consumerKey);

                    // Prevent negative values
                    if (newValue < 0)
                        await _db.StringSetAsync(consumerKey, 0);

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to decrement consumer job count for {WorkerId}/{JobType}", workerId, jobType);
                    throw;
                }
            },
            fallback: async () => true,
            operationName: "DecrementConsumerJobCount",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<bool> RemoveWorkerAsync(string workerId, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    var workerKey = $"workers:{workerId}";

                    var deleted = await _db.KeyDeleteAsync(workerKey);

                    if (deleted)
                        _logger.Information("Worker {WorkerId} removed from Redis", workerId);

                    return deleted;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to remove worker {WorkerId}", workerId);
                    return false;
                }
            },
            fallback: async () => false,
            operationName: "RemoveWorker",
            cancellationToken: cancellationToken
        );

    /// <summary>
    /// Implementation not needed - TTL handles zombie detection automatically
    /// </summary>
    /// <param name="threshold"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<List<string>> DetectZombieWorkersAsync(TimeSpan threshold, CancellationToken cancellationToken = default) => [];

    /// <summary>
    /// Safely deserializes a JSON string to Dictionary, handling empty/null/invalid values.
    /// </summary>
    private static Dictionary<string, string> DeserializeDictionaryOrDefault(string json)
    {
        if (string.IsNullOrEmpty(json) || json == "[]" || json == "null")
            return [];

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }
}

/// <summary>
/// Worker metadata structure (deserialized from JSON).
/// </summary>
internal class WorkerMetadata
{
    public int ProcessorCount { get; set; }
    public string OSVersion { get; set; }
    public string RuntimeVersion { get; set; }
    public List<JobConfigMetadata> JobConfigs { get; set; }
}

/// <summary>
/// Job consumer configuration metadata.
/// </summary>
internal class JobConfigMetadata
{
    public string JobType { get; set; }
    public string ConsumerId { get; set; }
    public int MaxParallelJobs { get; set; }
    public int ExecutionTimeoutSeconds { get; set; }
}
