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
    private readonly IRedisCircuitBreaker _circuitBreaker = circuitBreaker;
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<RedisWorkerService>();
    private readonly IDatabase _db = redis.GetDatabase();
    private readonly TimeSpan _instanceTTL = TimeSpan.FromMinutes(2); // Auto-expire zombie instances
    private readonly TimeSpan _workerMetadataTTL = TimeSpan.FromMinutes(5); // Worker metadata expires if no active instances
    private const string _workersIndexKey = "workers:index";

    /// <inheritdoc/>
    public Task<bool> RegisterWorkerAsync(WorkerDiscoveryRequest registration, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                var workerKey = $"workers:{registration.WorkerId}";
                var instanceKey = $"workers:{registration.WorkerId}:instances:{registration.InstanceId}";
                var instanceSetKey = $"workers:{registration.WorkerId}:instances";

                // Check if worker already exists to preserve registeredAt
                var existingRegisteredAt = await _db.HashGetAsync(workerKey, "registeredAt");
                var registeredAt = existingRegisteredAt.HasValue && !existingRegisteredAt.IsNullOrEmpty
                    ? existingRegisteredAt.ToString()
                    : DateTime.UtcNow.ToString("O");

                // Check if instance already exists to preserve registeredAt (before creating batch)
                var existingInstanceRegisteredAt = await _db.HashGetAsync(instanceKey, "registeredAt");
                var instanceRegisteredAt = existingInstanceRegisteredAt.HasValue && !existingInstanceRegisteredAt.IsNullOrEmpty
                    ? existingInstanceRegisteredAt.ToString()
                    : DateTime.UtcNow.ToString("O");

                var batch = _db.CreateBatch();

                // 1. Worker Metadata
                var workerData = new HashEntry[]
                {
                    new("displayName", registration.DisplayName ?? string.Empty),
                    new("routingPatterns", registration.RoutingPatterns != null ? JsonSerializer.Serialize(registration.RoutingPatterns) : "{}"),
                    new("jobDataDefinitions", registration.JobDataDefinitions != null ? JsonSerializer.Serialize(registration.JobDataDefinitions) : "{}"),
                    new("jobResultDefinitions", registration.JobResultDefinitions != null ? JsonSerializer.Serialize(registration.JobResultDefinitions) : "{}"),
                    new("jobNames", registration.JobTypes != null ? JsonSerializer.Serialize(registration.JobTypes) : "[]"),
                    new("maxParallelJobs", registration.MaxParallelJobs),
                    new("version", registration.Version ?? string.Empty),
                    new("metadata", !string.IsNullOrEmpty(registration.Metadata) ? registration.Metadata : "{}"),
                    new("registeredAt", registeredAt ?? string.Empty),
                };

                var batchTasks = new List<Task>
                {
                    batch.HashSetAsync(workerKey, workerData),
                    batch.KeyExpireAsync(workerKey, _workerMetadataTTL)
                };

                // 2. Instance Data
                var instanceData = new HashEntry[]
                {
                    new("hostName", registration.HostName ?? string.Empty),
                    new("currentJobs", 0),
                    new("status", WorkerStatus.Active.ToString()),
                    new("lastHeartbeat", DateTime.UtcNow.ToString("O")),
                    new("registeredAt", instanceRegisteredAt ?? string.Empty), // Preserve original instance registration time
                    new("ipAddress", registration.IpAddress ?? string.Empty)
                };

                batchTasks.Add(batch.HashSetAsync(instanceKey, instanceData));
                batchTasks.Add(batch.KeyExpireAsync(instanceKey, _instanceTTL));

                // 3. Indexes (SETs)
                batchTasks.Add(batch.SetAddAsync(_workersIndexKey, registration.WorkerId));
                batchTasks.Add(batch.SetAddAsync(instanceSetKey, registration.InstanceId));
                batchTasks.Add(batch.KeyExpireAsync(instanceSetKey, _workerMetadataTTL));

                batch.Execute();
                await Task.WhenAll(batchTasks);

                var isNewWorker = !existingRegisteredAt.HasValue || existingRegisteredAt.IsNullOrEmpty;
                var isNewInstance = !existingInstanceRegisteredAt.HasValue || existingInstanceRegisteredAt.IsNullOrEmpty;

                if (isNewWorker && isNewInstance)
                    _logger.Information("[REDIS] New worker registered: {WorkerId}:{InstanceId} (TTL: {TTLMinutes}min, Host: {HostName})", registration.WorkerId, registration.InstanceId, _instanceTTL.TotalMinutes, registration.HostName);
                else if (isNewInstance)
                    _logger.Information("[REDIS] New instance registered for existing worker: {WorkerId}:{InstanceId} (TTL: {TTLMinutes}min, Host: {HostName})", registration.WorkerId, registration.InstanceId, _instanceTTL.TotalMinutes, registration.HostName);
                else
                    _logger.Debug("[REDIS] Worker metadata refreshed: {WorkerId}:{InstanceId} (TTL: {TTLMinutes}min)", registration.WorkerId, registration.InstanceId, _instanceTTL.TotalMinutes);

                return await Task.FromResult(true);
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
                var instanceSetKey = $"workers:{workerId}:instances";

                // Check if instance exists
                var instanceExists = await _db.KeyExistsAsync(instanceKey);
                if (!instanceExists)
                {
                    _logger.Warning("[REDIS] UpdateHeartbeat FAILED: Instance key '{InstanceKey}' does not exist. Instance may have expired due to missing heartbeats.", instanceKey);
                    return false;
                }

                // Update heartbeat and current jobs
                await _db.HashSetAsync(instanceKey,
                [
                    new HashEntry("lastHeartbeat", DateTime.UtcNow.ToString("O")),
                    new HashEntry("currentJobs", currentJobs),
                    new HashEntry("status", WorkerStatus.Active.ToString())
                ]);

                // Re-add to indexes to ensure consistency
                await _db.SetAddAsync(_workersIndexKey, workerId);
                await _db.SetAddAsync(instanceSetKey, instanceId);

                // Refresh TTL on instance, worker metadata, and instance SET (keep alive while instances are active)
                var instanceTtlSet = await _db.KeyExpireAsync(instanceKey, _instanceTTL);
                var workerTtlSet = await _db.KeyExpireAsync(workerKey, _workerMetadataTTL);
                var instanceSetTtlSet = await _db.KeyExpireAsync(instanceSetKey, _workerMetadataTTL);

                if (!instanceTtlSet || !workerTtlSet || !instanceSetTtlSet)
                {
                    _logger.Warning("[REDIS] UpdateHeartbeat: TTL refresh failed for {InstanceKey}. InstanceTTL={InstanceTtlSet}, WorkerTTL={WorkerTtlSet}, InstanceSetTTL={InstanceSetTtlSet}", instanceKey, instanceTtlSet, workerTtlSet, instanceSetTtlSet);
                }

                _logger.Debug("[REDIS] UpdateHeartbeat SUCCESS: {InstanceKey} -> currentJobs={CurrentJobs}, TTL refreshed to {TTLMinutes}min", instanceKey, currentJobs, _instanceTTL.TotalMinutes);

                return true;
            },
            fallback: async () =>
            {
                _logger.Error("[REDIS] UpdateHeartbeat circuit breaker triggered for {WorkerId}:{InstanceId}", workerId, instanceId);
                return false;
            },
            operationName: "UpdateHeartbeat",
            cancellationToken: cancellationToken
        );

    /// <summary>
    /// Updates heartbeats for multiple worker instances in batch.
    /// </summary>
    public async Task<int> BulkUpdateHeartbeatsAsync(List<(string WorkerId, string InstanceId, int CurrentJobs, long MemoryBytes, double CpuUsagePercent, DateTime Timestamp)> updates, CancellationToken cancellationToken = default)
    {
        if (updates.IsNullOrEmpty())
            return 0;

        try
        {
            var batch = _db.CreateBatch();
            var updateTasks = new List<Task>();

            foreach (var (WorkerId, InstanceId, CurrentJobs, MemoryBytes, CpuUsagePercent, Timestamp) in updates)
            {
                var instanceKey = $"workers:{WorkerId}:instances:{InstanceId}";
                var workerKey = $"workers:{WorkerId}";
                var instanceSetKey = $"workers:{WorkerId}:instances";

                var heartbeatTime = Timestamp.ToString("O");

                updateTasks.Add(batch.HashSetAsync(instanceKey,
                [
                    new("lastHeartbeat", heartbeatTime),
                    new("currentJobs", CurrentJobs),
                    new("memoryBytes", MemoryBytes),
                    new("cpuUsagePercent", CpuUsagePercent),
                    new("status", WorkerStatus.Active.ToString())
                ]));

                // Re-add to indexes to ensure consistency
                updateTasks.Add(batch.SetAddAsync(_workersIndexKey, WorkerId));
                updateTasks.Add(batch.SetAddAsync(instanceSetKey, InstanceId));

                updateTasks.Add(batch.KeyExpireAsync(instanceKey, _instanceTTL));
                updateTasks.Add(batch.KeyExpireAsync(workerKey, _workerMetadataTTL));
                updateTasks.Add(batch.KeyExpireAsync(instanceSetKey, _workerMetadataTTL));
            }

            batch.Execute();
            await Task.WhenAll(updateTasks);

            _logger.Debug("{Count} instances updated", updates.Count, updateTasks.Count);

            return updates.Count;
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
                var instanceSetKey = $"workers:{workerId}:instances";

                // Check if worker metadata exists
                if (!await _db.KeyExistsAsync(workerKey))
                    return null;

                // Get worker metadata
                var workerData = await _db.HashGetAllAsync(workerKey);

                if (workerData.Length == 0)
                    return null;

                var workerDict = workerData.ToDictionary(x => x.Name.ToString(), x => x.Value.ToString());

                // Get instance IDs
                var instanceIds = await _db.SetMembersAsync(instanceSetKey);

                if (instanceIds.Length == 0)
                {
                    _logger.Warning("[GetWorkerAsync] Worker {WorkerId} has metadata but no active instances in index. Removing stale metadata.", workerId);

                    try
                    {
                        await _db.KeyDeleteAsync(workerKey);
                        await _db.SetRemoveAsync("workers:index", workerId);
                        _logger.Information("Removed stale worker metadata for {WorkerId}", workerId);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Failed to remove stale metadata for {WorkerId}", workerId);
                    }

                    return null;
                }

                // Fetch all instance data in parallel
                var instanceKeys = instanceIds.Select(id => (RedisKey)$"workers:{workerId}:instances:{id}").ToArray();
                var instanceDataTasks = instanceKeys.Select(key => _db.HashGetAllAsync(key)).ToList();
                var instanceDataResults = await Task.WhenAll(instanceDataTasks);

                var instances = new List<WorkerInstance>();

                for (int i = 0; i < instanceIds.Length; i++)
                {
                    var instanceData = instanceDataResults[i];
                    var instanceId = instanceIds[i].ToString();

                    if (instanceData.Length == 0)
                    {
                        _logger.Debug("[GetWorkerAsync] Instance expired during read: {InstanceId}", instanceId);

                        // Remove from index
                        await _db.SetRemoveAsync(instanceSetKey, instanceId);
                        continue;
                    }

                    var instanceDict = instanceData.ToDictionary(x => x.Name.ToString(), x => x.Value.ToString());

                    // Skip instances with invalid data
                    var lastHeartbeatStr = instanceDict.GetValueOrDefault("lastHeartbeat");
                    var registeredAtStr = instanceDict.GetValueOrDefault("registeredAt");

                    if (string.IsNullOrWhiteSpace(lastHeartbeatStr) || !DateTime.TryParse(lastHeartbeatStr, out var lastHeartbeat))
                    {
                        _logger.Warning("[GetWorkerAsync] Instance {InstanceId} has invalid lastHeartbeat: {Value}. Skipping.", instanceId, lastHeartbeatStr);
                        await _db.SetRemoveAsync(instanceSetKey, instanceId);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(registeredAtStr) || !DateTime.TryParse(registeredAtStr, out var registeredAt))
                    {
                        _logger.Warning("[GetWorkerAsync] Instance {InstanceId} has invalid registeredAt: {Value}. Using lastHeartbeat as fallback.", instanceId, registeredAtStr);
                        registeredAt = lastHeartbeat;
                    }

                    var instance = new WorkerInstance
                    {
                        InstanceId = instanceId,
                        HostName = instanceDict.GetValueOrDefault("hostName"),
                        IpAddress = instanceDict.GetValueOrDefault("ipAddress"),
                        CurrentJobs = int.Parse(instanceDict.GetValueOrDefault("currentJobs", "0")),
                        MemoryBytes = long.Parse(instanceDict.GetValueOrDefault("memoryBytes", "0")),
                        CpuUsagePercent = double.Parse(instanceDict.GetValueOrDefault("cpuUsagePercent", "0"), System.Globalization.CultureInfo.InvariantCulture),
                        Status = Enum.Parse<WorkerStatus>(instanceDict.GetValueOrDefault("status", "Active")),
                        LastHeartbeat = lastHeartbeat,
                        RegisteredAt = registeredAt
                    };

                    instances.Add(instance);
                }

                // If no active instances found after pipeline fetch, remove stale metadata
                if (instances.IsNullOrEmpty())
                {
                    _logger.Warning("[GetWorkerAsync] Worker {WorkerId} metadata exists but all instances expired. Removing stale metadata.", workerId);

                    try
                    {
                        await _db.KeyDeleteAsync(workerKey);
                        await _db.KeyDeleteAsync(instanceSetKey);
                        await _db.SetRemoveAsync("workers:index", workerId);
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
                    JobResultDefinitions = DeserializeDictionaryOrDefault(workerDict.GetValueOrDefault("jobResultDefinitions")),
                    JobNames = JsonSerializer.Deserialize<List<string>>(workerDict.GetValueOrDefault("jobNames", "[]")),
                    MaxParallelJobs = int.Parse(workerDict.GetValueOrDefault("maxParallelJobs", "0")),
                    Version = workerDict.GetValueOrDefault("version"),
                    Metadata = JsonSerializer.Deserialize<WorkerMetadata>(workerDict.GetValueOrDefault("metadata", "{}")),
                    RegisteredAt = DateTime.Parse(workerDict.GetValueOrDefault("registeredAt", DateTime.UtcNow.ToString("O"))),
                    Status = instances.Any(i => i.Status == WorkerStatus.Active) ? WorkerStatus.Active : WorkerStatus.Zombie,
                    LastHeartbeat = !instances.IsNullOrEmpty() ? instances.Max(i => i.LastHeartbeat) : null,
                    CurrentJobs = instances.Sum(i => i.CurrentJobs),
                    MemoryBytes = instances.Sum(i => i.MemoryBytes),
                    CpuUsagePercent = instances.Count > 0 ? Math.Round(instances.Average(i => i.CpuUsagePercent), 2) : 0,
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
                // O(1) fetch from index set
                var workerIds = await _db.SetMembersAsync(_workersIndexKey);
                if (workerIds.IsNullOrEmpty())
                    return [];

                // Phase 1: Fetch worker metadata and instance sets
                var batch1 = _db.CreateBatch();

                var metadataTasks = workerIds.ToDictionary(id => id.ToString(), id => batch1.HashGetAllAsync($"workers:{id}"));
                var instanceSetTasks = workerIds.ToDictionary(id => id.ToString(), id => batch1.SetMembersAsync($"workers:{id}:instances"));

                batch1.Execute();
                await Task.WhenAll(metadataTasks.Values.Concat(instanceSetTasks.Values.Cast<Task>()));

                // Phase 2: Fetch all instance data
                var batch2 = _db.CreateBatch();
                var instanceDataTasks = new Dictionary<string, Task<HashEntry[]>>();

                foreach (var workerId in workerIds.Select(x => x.ToString()))
                {
                    var instanceIds = await instanceSetTasks[workerId];

                    foreach (var instanceId in instanceIds)
                    {
                        var instanceKey = $"workers:{workerId}:instances:{instanceId}";
                        instanceDataTasks[instanceKey] = batch2.HashGetAllAsync(instanceKey);
                    }
                }

                batch2.Execute();
                await Task.WhenAll(instanceDataTasks.Values);

                // Build worker objects
                var workers = new List<CachedWorker>();

                foreach (var workerId in workerIds.Select(x => x.ToString()))
                {
                    var meta = await metadataTasks[workerId];
                    if (meta.IsNullOrEmpty())
                        continue;

                    var metaDict = meta.ToDictionary(x => x.Name.ToString(), x => x.Value.ToString());

                    var instanceIds = await instanceSetTasks[workerId];
                    var instances = new List<WorkerInstance>();

                    foreach (var instanceId in instanceIds)
                    {
                        var instanceKey = $"workers:{workerId}:instances:{instanceId}";

                        if (!instanceDataTasks.TryGetValue(instanceKey, out var instanceTask))
                            continue;

                        var instanceData = await instanceTask;

                        if (instanceData.IsNullOrEmpty())
                            continue;

                        var instanceDict = instanceData.ToDictionary(x => x.Name.ToString(), x => x.Value.ToString());

                        // Skip instances with invalid data
                        var lastHeartbeatStr = instanceDict.GetValueOrDefault("lastHeartbeat");
                        var registeredAtStr = instanceDict.GetValueOrDefault("registeredAt");

                        if (string.IsNullOrWhiteSpace(lastHeartbeatStr) || !DateTime.TryParse(lastHeartbeatStr, out var lastHeartbeat))
                        {
                            _logger.Warning("[GetAllWorkersAsync] Instance {InstanceId} has invalid lastHeartbeat: {Value}. Skipping.", instanceId, lastHeartbeatStr);
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(registeredAtStr) || !DateTime.TryParse(registeredAtStr, out var registeredAt))
                        {
                            registeredAt = lastHeartbeat;
                        }

                        instances.Add(new WorkerInstance
                        {
                            InstanceId = instanceId.ToString(),
                            HostName = instanceDict.GetValueOrDefault("hostName"),
                            IpAddress = instanceDict.GetValueOrDefault("ipAddress"),
                            CurrentJobs = int.Parse(instanceDict.GetValueOrDefault("currentJobs", "0")),
                            MemoryBytes = long.Parse(instanceDict.GetValueOrDefault("memoryBytes", "0")),
                            CpuUsagePercent = double.Parse(instanceDict.GetValueOrDefault("cpuUsagePercent", "0"), System.Globalization.CultureInfo.InvariantCulture),
                            Status = Enum.Parse<WorkerStatus>(instanceDict.GetValueOrDefault("status", "Active")),
                            LastHeartbeat = lastHeartbeat,
                            RegisteredAt = registeredAt
                        });
                    }

                    if (instances.IsNullOrEmpty())
                        continue;

                    workers.Add(new CachedWorker
                    {
                        WorkerId = workerId,
                        DisplayName = metaDict.GetValueOrDefault("displayName"),
                        RoutingPatterns = DeserializeDictionaryOrDefault(metaDict.GetValueOrDefault("routingPatterns")),
                        JobDataDefinitions = DeserializeDictionaryOrDefault(metaDict.GetValueOrDefault("jobDataDefinitions")),
                        JobResultDefinitions = DeserializeDictionaryOrDefault(metaDict.GetValueOrDefault("jobResultDefinitions")),
                        JobNames = JsonSerializer.Deserialize<List<string>>(metaDict.GetValueOrDefault("jobNames", "[]")),
                        MaxParallelJobs = int.Parse(metaDict.GetValueOrDefault("maxParallelJobs", "0")),
                        Version = metaDict.GetValueOrDefault("version"),
                        Metadata = JsonSerializer.Deserialize<WorkerMetadata>(metaDict.GetValueOrDefault("metadata", "{}")),
                        RegisteredAt = DateTime.Parse(metaDict.GetValueOrDefault("registeredAt", DateTime.UtcNow.ToString("O"))),
                        Status = instances.Any(i => i.Status == WorkerStatus.Active) ? WorkerStatus.Active : WorkerStatus.Zombie,
                        LastHeartbeat = instances.Max(i => i.LastHeartbeat),
                        CurrentJobs = instances.Sum(i => i.CurrentJobs),
                        MemoryBytes = instances.Sum(i => i.MemoryBytes),
                        CpuUsagePercent = instances.Count > 0 ? Math.Round(instances.Average(i => i.CpuUsagePercent), 2) : 0,
                        Instances = instances
                    });
                }

                _logger.Debug("Fetched {Count} workers with {InstanceCount} total instances", workers.Count, workers.Sum(w => w.Instances.Count));

                return workers;
            },
            fallback: async () => [],
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
                    var workerKey = $"workers:{workerId}";
                    var jobCountsKey = $"workers:{workerId}:job_counts";

                    // Get worker metadata to extract consumer-specific MaxParallelJobs
                    var metadataValue = await _db.HashGetAsync(workerKey, "metadata");

                    int? consumerMaxParallel = null;

                    if (metadataValue.HasValue && !string.IsNullOrEmpty(metadataValue.ToString()))
                    {
                        try
                        {
                            var metadata = JsonSerializer.Deserialize<WorkerMetadata>(metadataValue.ToString());
                            var consumerConfig = metadata?.JobConfigs?.FirstOrDefault(c => c.JobType == jobType);

                            if (consumerConfig != null)
                                consumerMaxParallel = consumerConfig.MaxParallelJobs;
                        }
                        catch
                        {
                            // Metadata parse failed, ignore
                        }
                    }

                    // Get current job count for this specific consumer (jobType)
                    var currentCount = await _db.HashGetAsync(jobCountsKey, jobType);

                    int current = currentCount.HasValue && int.TryParse(currentCount.ToString(), out var c) ? c : 0;

                    return (current, consumerMaxParallel);
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
                var key = $"workers:{workerId}:job_counts";
                await _db.HashIncrementAsync(key, jobType, 1);
                await _db.KeyExpireAsync(key, _workerMetadataTTL);
                return true;
            },
            fallback: async () => true,
            operationName: "IncrementConsumer",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task DecrementConsumerJobCountAsync(string workerId, string jobType, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                var key = $"workers:{workerId}:job_counts";
                var newVal = await _db.HashIncrementAsync(key, jobType, -1);

                if (newVal < 0)
                    await _db.HashSetAsync(key, jobType, 0);

                return true;
            },
            fallback: async () => true,
            operationName: "DecrementConsumer",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task BatchUpdateConsumerJobCountsAsync(Dictionary<string, int> updates, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                if (updates.IsNullOrEmpty())
                    return true;

                try
                {
                    // Group updates by workerId:instanceId (each instance has its own job_counts HASH)
                    // Key format from StatusTrackerService: "workerId:instanceId:jobType"
                    var instanceGroups = new Dictionary<string, Dictionary<string, int>>();

                    foreach (var (key, netChange) in updates)
                    {
                        // key format: "workerId:instanceId:jobType"
                        var parts = key.Split(':');
                        if (parts.Length != 3)
                            continue;

                        var workerId = parts[0];
                        var instanceId = parts[1];
                        var jobType = parts[2];

                        // Group key: "workerId:instanceId"
                        var groupKey = $"{workerId}:{instanceId}";

                        if (!instanceGroups.ContainsKey(groupKey))
                            instanceGroups[groupKey] = [];

                        instanceGroups[groupKey][jobType] = netChange;
                    }

                    // Lua script for HASH-based batch update with floor check
                    var luaScript = @"
                        local hashKey = KEYS[1]
                        local argIndex = 1

                        while argIndex <= #ARGV do
                            local jobType = ARGV[argIndex]
                            local netChange = tonumber(ARGV[argIndex + 1])

                            if netChange ~= 0 then
                                local current = tonumber(redis.call('HGET', hashKey, jobType) or 0)
                                local newValue = current + netChange

                                if newValue < 0 then
                                    newValue = 0
                                end

                                redis.call('HSET', hashKey, jobType, newValue)
                            end

                            argIndex = argIndex + 2
                        end

                        redis.call('EXPIRE', hashKey, 3600)
                        return 1";

                    // Execute Lua script for each instance's HASH
                    foreach (var (groupKey, jobUpdates) in instanceGroups)
                    {
                        // groupKey is "workerId:instanceId"
                        var parts = groupKey.Split(':');
                        var workerId = parts[0];
                        var instanceId = parts[1];

                        // Key pattern: workers:{workerId}:instances:{instanceId}:job_counts
                        var hashKey = $"workers:{workerId}:instances:{instanceId}:job_counts";
                        var keys = new RedisKey[] { hashKey };

                        // Build ARGV array: [jobType1, netChange1, jobType2, netChange2, ...]
                        var args = new List<RedisValue>();
                        foreach (var (jobType, netChange) in jobUpdates)
                        {
                            args.Add(jobType);
                            args.Add(netChange);
                        }

                        await _db.ScriptEvaluateAsync(luaScript, keys, [.. args]);
                    }

                    _logger.Debug("Batch updated consumer counters for {InstanceCount} instances ({TotalUpdates} total updates)", instanceGroups.Count, updates.Count);

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to batch update consumer job counts");
                    throw;
                }
            },
            fallback: async () => true,
            operationName: "BatchUpdateConsumerJobCounts",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task<bool> RemoveWorkerAsync(string workerId, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    var workerKey = $"workers:{workerId}";
                    var instanceSetKey = $"workers:{workerId}:instances";

                    // ✅ Get all instance IDs from INDEX SET
                    var instanceIds = await _db.SetMembersAsync(instanceSetKey);

                    // Delete all instance keys
                    if (!instanceIds.IsNullOrEmpty())
                    {
                        var instanceKeys = instanceIds.Select(id => (RedisKey)$"workers:{workerId}:instances:{id}").ToArray();
                        await _db.KeyDeleteAsync(instanceKeys);
                    }

                    // Delete worker metadata
                    await _db.KeyDeleteAsync(workerKey);

                    // Delete instance SET
                    await _db.KeyDeleteAsync(instanceSetKey);

                    // ✅ Remove from worker index
                    await _db.SetRemoveAsync("workers:index", workerId);

                    _logger.Information("Worker {WorkerId} and {InstanceCount} instances removed from Redis", workerId, instanceIds.Length);

                    return true;
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

    /// <inheritdoc/>
    public Task<bool> RemoveWorkerInstanceAsync(string workerId, string instanceId, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    var instanceKey = $"workers:{workerId}:instances:{instanceId}";
                    var instanceSetKey = $"workers:{workerId}:instances";

                    // Delete instance metadata
                    await _db.KeyDeleteAsync(instanceKey);

                    // ✅ Remove from instance index SET
                    await _db.SetRemoveAsync(instanceSetKey, instanceId);

                    // ✅ Clean up consumer count key for this instance (single key, no SCAN!)
                    // Pattern changed: consumer:{workerId}:{jobType}:count (shared across all instances)
                    // No per-instance keys anymore, so no cleanup needed here

                    _logger.Information("Worker instance {InstanceId} removed from Redis", instanceId);

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to remove worker instance {InstanceId}", instanceId);
                    return false;
                }
            },
            fallback: async () => false,
            operationName: "RemoveWorkerInstance",
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
