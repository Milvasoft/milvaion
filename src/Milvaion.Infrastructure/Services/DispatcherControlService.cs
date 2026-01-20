using Microsoft.Extensions.Logging;
using Milvaion.Application.Interfaces;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Utils;

namespace Milvaion.Infrastructure.Services;

/// <summary>
/// Implementation of dispatcher control service.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DispatcherControlService"/> class.
/// </remarks>
public class DispatcherControlService(ILoggerFactory loggerFactory) : IDispatcherControlService
{
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<DispatcherControlService>();
    private volatile bool _enabled = true;
    private string _stopReason;
    private DateTime? _stoppedAt;
    private string _stoppedBy;

    /// <inheritdoc/>
    public bool IsEnabled => _enabled;

    /// <inheritdoc/>
    public void Stop(string reason, string username)
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

        _logger.Fatal("?? EMERGENCY STOP activated by {User}. Reason: {Reason}. Dispatcher will pause after current cycle.", username, reason);
    }

    /// <inheritdoc/>
    public void Resume(string username)
    {
        if (_enabled)
        {
            _logger.Warning("Dispatcher already running");
            return;
        }

        var downtime = _stoppedAt.HasValue ? DateTime.UtcNow - _stoppedAt.Value : TimeSpan.Zero;

        var previousUser = _stoppedBy;

        _enabled = true;
        _stopReason = null;
        _stoppedAt = null;
        _stoppedBy = null;

        _logger.Information("? System RESUMED by {User}. Was stopped by {PreviousUser} for {Downtime}. Dispatcher will restart.", username, previousUser, downtime);
    }

    /// <inheritdoc/>
    public string GetStopReason()
    {
        if (_enabled)
            return null;

        return $"Stopped by {_stoppedBy} at {_stoppedAt:yyyy-MM-dd HH:mm:ss} UTC. Reason: {_stopReason}";
    }
}
