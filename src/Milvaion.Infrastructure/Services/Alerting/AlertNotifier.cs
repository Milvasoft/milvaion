using Microsoft.Extensions.Options;
using Milvaion.Application.Dtos.AlertingDtos;
using Milvaion.Application.Interfaces;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Infrastructure.Services.Alerting;

/// <summary>
/// Orchestrates sending alerts through multiple configured channels.
/// All operations are designed to be non-blocking and resource-efficient.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="AlertNotifier"/> class.
/// </remarks>
public class AlertNotifier(IOptions<AlertingOptions> options,
                           IEnumerable<IAlertChannel> channels,
                           Lazy<IMilvaLogger> logger) : IAlertNotifier
{
    private readonly AlertingOptions _options = options.Value;
    private readonly Lazy<IMilvaLogger> _logger = logger;
    private readonly Dictionary<string, IAlertChannel> _channelMap = channels.ToDictionary(c => c.ChannelName, c => c, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Default timeout for alert operations (10 seconds).
    /// </summary>
    private static readonly TimeSpan _defaultTimeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc/>
    public async Task<AlertResult> SendAsync(AlertType alertType, AlertPayload payload, CancellationToken cancellationToken = default)
    {
        // Check if alert type is enabled
        if (!IsAlertEnabled(alertType))
            return AlertResult.Skipped($"Alert type '{alertType}' is disabled");

        var routes = GetRoutesForAlert(alertType);

        if (routes.Count == 0)
            return AlertResult.Skipped($"No routes configured for alert type '{alertType}'");

        // Build full action URL if relative path is provided
        if (!string.IsNullOrWhiteSpace(payload.ActionLink))
            payload.ActionLink = _options.BuildActionUrl(payload.ActionLink);

        var channelResults = new List<ChannelResult>();
        var tasks = new List<Task<ChannelResult>>();

        // Create a linked token with timeout to prevent hanging
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            timeoutCts.CancelAfter(_defaultTimeout);

            foreach (var route in routes)
            {
                if (_channelMap.TryGetValue(route, out var channel))
                {
                    tasks.Add(SendToChannelWithTimeoutAsync(channel, alertType, payload, timeoutCts.Token));
                }
                else
                {
                    channelResults.Add(ChannelResult.Skipped(route, $"Channel '{route}' not registered"));
                }
            }

            if (tasks.Count > 0)
            {
                // Wait for all tasks, don't throw on individual failures
                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                channelResults.AddRange(results);
            }
        }
        finally
        {
            timeoutCts.Dispose();
        }

        var hasAnySuccess = channelResults.Exists(r => r.Success);

        return hasAnySuccess ? AlertResult.Successful(channelResults) : AlertResult.Failed(channelResults);
    }

    /// <inheritdoc/>
    public void SendFireAndForget(AlertType alertType, AlertPayload payload)
    {
        // Quick check before spawning task
        if (!IsAlertEnabled(alertType))
            return;

        var routes = GetRoutesForAlert(alertType);

        if (routes.Count == 0)
            return;

        // Fire and forget - spawn task without awaiting
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(_defaultTimeout);
                await SendAsync(alertType, payload, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Silently log, never throw from fire-and-forget
                try
                {
                    _logger.Value.Warning(ex, "Fire-and-forget alert failed for {AlertType}: {Message}", alertType, ex.Message);
                }
                catch
                {
                    // Ignore logging failures
                }
            }
        });
    }

    /// <inheritdoc/>
    public bool IsAlertEnabled(AlertType alertType)
    {
        if (_options.Alerts == null || !_options.Alerts.TryGetValue(alertType, out var alertConfig))
            return true; // Default to enabled if not configured

        return alertConfig.Enabled;
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> GetRoutesForAlert(AlertType alertType)
    {
        if (_options.Alerts != null && _options.Alerts.TryGetValue(alertType, out var alertConfig) && alertConfig.Routes?.Count > 0)
            return alertConfig.Routes;

        // Return default channel if no specific routes configured
        return string.IsNullOrWhiteSpace(_options.DefaultChannel) ? [] : [_options.DefaultChannel];
    }

    private async Task<ChannelResult> SendToChannelWithTimeoutAsync(IAlertChannel channel,
                                                                    AlertType alertType,
                                                                    AlertPayload payload,
                                                                    CancellationToken cancellationToken)
    {
        try
        {
            return await channel.SendAsync(alertType, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ChannelResult.Failed(channel.ChannelName, "Operation timed out or was cancelled");
        }
        catch (Exception ex)
        {
            _logger.Value.Warning(ex, "Failed to send alert through channel {ChannelName}: {Message}", channel.ChannelName, ex.Message);
            return ChannelResult.Failed(channel.ChannelName, ex.Message, ex);
        }
    }
}
