using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces;
using Milvaion.Infrastructure.Services.Redis;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Utils;
using StackExchange.Redis;
using System.Text.Json;

namespace Milvaion.Infrastructure.Services;

/// <summary>
/// Implementation of dispatcher control service. The stop state lives in Redis so that an emergency stop
/// reaches every API instance and survives a restart; each instance keeps a local copy to read cheaply.
/// </summary>
public class DispatcherControlService(ILoggerFactory loggerFactory,
                                      IOptions<JobDispatcherOptions> dispatcherOptions,
                                      RedisConnectionService redisConnection = null,
                                      IOptions<RedisOptions> redisOptions = null) : IDispatcherControlService
{
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<DispatcherControlService>();
    private readonly RedisConnectionService _redisConnection = redisConnection;
    private readonly string _stateKey = $"{redisOptions?.Value.KeyPrefix}dispatcher:emergency_stop";
    private readonly Lock _stateLock = new();
    private readonly bool _enabledByConfiguration = dispatcherOptions.Value.Enabled;
    private volatile bool _enabled = dispatcherOptions.Value.Enabled;
    private string _stopReason;
    private DateTime? _stoppedAt;
    private string _stoppedBy;

    // Set while a local stop or resume has not reached Redis yet. Until it does, Redis is out of date
    // and must not overwrite the local decision.
    private volatile bool _pendingPersist;

    /// <inheritdoc/>
    public bool IsEnabled => _enabled;

    /// <inheritdoc/>
    public void Stop(string reason, string username)
    {
        lock (_stateLock)
        {
            if (!_enabled)
            {
                _logger.Warning("Dispatcher already stopped by {User} at {Time}", _stoppedBy, _stoppedAt);
                return;
            }

            _enabled = false;
            _stopReason = reason;
            _stoppedAt = DateTime.UtcNow;
            _stoppedBy = username;
            _pendingPersist = true;
        }

        _logger.Fatal("EMERGENCY STOP activated by {User}. Reason: {Reason}. Dispatcher will pause after current cycle.", username, reason);

        TryPersist();
    }

    /// <inheritdoc/>
    public void Resume(string username)
    {
        string previousUser;
        TimeSpan downtime;

        lock (_stateLock)
        {
            if (_enabled)
            {
                _logger.Warning("Dispatcher already running");
                return;
            }

            downtime = _stoppedAt.HasValue ? DateTime.UtcNow - _stoppedAt.Value : TimeSpan.Zero;
            previousUser = _stoppedBy;

            _enabled = true;
            _stopReason = null;
            _stoppedAt = null;
            _stoppedBy = null;
            _pendingPersist = true;
        }

        _logger.Information("System RESUMED by {User}. Was stopped by {PreviousUser} for {Downtime}. Dispatcher will restart.", username, previousUser, downtime);

        TryPersist();
    }

    /// <inheritdoc/>
    public string GetStopReason()
    {
        lock (_stateLock)
        {
            if (_enabled)
                return null;

            return $"Stopped by {_stoppedBy} at {_stoppedAt:yyyy-MM-dd HH:mm:ss} UTC. Reason: {_stopReason}";
        }
    }

    /// <inheritdoc/>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_redisConnection == null)
            return;

        if (_pendingPersist)
        {
            TryPersist();

            return;
        }

        try
        {
            var value = await _redisConnection.Database.StringGetAsync(_stateKey);
            var state = value.HasValue ? JsonSerializer.Deserialize<StopState>(value.ToString()) : null;

            ApplySharedState(state);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or JsonException)
        {
            _logger.Warning(ex, "Could not read the emergency stop state from Redis. Keeping the last known state.");
        }
    }

    private void ApplySharedState(StopState state)
    {
        lock (_stateLock)
        {
            // A local change raced this read; the next refresh sees it.
            if (_pendingPersist)
                return;

            // An instance switched off by configuration stays off; the shared state only carries the emergency stop.
            var shouldBeEnabled = state == null && _enabledByConfiguration;

            if (shouldBeEnabled == _enabled)
                return;

            _enabled = shouldBeEnabled;
            _stopReason = state?.Reason;
            _stoppedAt = state?.StoppedAt;
            _stoppedBy = state?.StoppedBy;

            _logger.Warning("Emergency stop state changed through another instance. Dispatcher enabled: {Enabled}", shouldBeEnabled);
        }
    }

    private void TryPersist()
    {
        if (_redisConnection == null)
        {
            _pendingPersist = false;
            return;
        }

        try
        {
            StopState state;

            lock (_stateLock)
            {
                state = _enabled ? null : new StopState(_stopReason, _stoppedAt, _stoppedBy);
            }

            // Synchronous on purpose: this runs from the admin endpoint and the operator should learn right away if it failed.
            if (state == null)
                _redisConnection.Database.KeyDelete(_stateKey);
            else
                _redisConnection.Database.StringSet(_stateKey, JsonSerializer.Serialize(state));

            _pendingPersist = false;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.Error(ex, "Could not store the emergency stop state in Redis. Other instances are not affected until it is stored; retrying on the next dispatcher cycle.");
        }
    }

    private sealed record StopState(string Reason, DateTime? StoppedAt, string StoppedBy);
}
