using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Milvaion.Application.Dtos.AlertingDtos;
using Milvaion.Application.Interfaces;
using Milvaion.Domain;
using Milvaion.Infrastructure.Persistence.Context;
using StackExchange.Redis;

namespace Milvaion.Infrastructure.BackgroundServices;

/// <summary>
/// Periodically scans API keys and raises an alert as each one approaches, then passes, its
/// expiry. Fires <see cref="AlertType.ApiKeyExpiring"/> within a window before the date and
/// <see cref="AlertType.ApiKeyExpired"/> once it has passed.
///
/// De-duplicated through Redis: a short-lived key per (api key, state) means the same alert is
/// raised at most once per day, across every instance, rather than every scan.
///
/// Deliberately invisible: it is a plain background service, not registered with the memory or
/// metrics registries, so it never appears on the monitoring or configuration screens. Every
/// operation is wrapped so a failure is swallowed quietly (debug-level only) and never disturbs
/// the rest of the system - the only thing a user ever sees is the notification itself.
/// </summary>
public class ApiKeyExpiryMonitorService(IServiceScopeFactory scopeFactory,
                                        IAlertNotifier alertNotifier,
                                        IConnectionMultiplexer redis,
                                        ILogger<ApiKeyExpiryMonitorService> logger) : BackgroundService
{
    /// <summary>How long before expiry a key is considered "expiring".</summary>
    private const int _expiringWindowDays = 7;

    /// <summary>How often the scan runs. Expiry is day-granular, so a few times a day is plenty.</summary>
    private static readonly TimeSpan _checkInterval = TimeSpan.FromHours(6);

    /// <summary>Suppression window per key+state, so an alert repeats at most once a day.</summary>
    private static readonly TimeSpan _dedupTtl = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IAlertNotifier _alertNotifier = alertNotifier;
    private readonly IConnectionMultiplexer _redis = redis;
    private readonly ILogger<ApiKeyExpiryMonitorService> _logger = logger;

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app finish starting before the first scan.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Quiet by design: debug-level only, so a transient failure never surfaces to the
                // user or pollutes error logs. The next scan retries.
                _logger.LogDebug(ex, "API key expiry check failed.");
            }

            try
            {
                await Task.Delay(_checkInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var window = now.AddDays(_expiringWindowDays);

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

        // Active (not revoked) keys that have an expiry which is already past or within the window.
        var keys = await dbContext.ApiKeys.AsNoTracking().Where(k => k.RevokedAt == null && k.ExpiresAt != null && k.ExpiresAt <= window).ToListAsync(cancellationToken);

        if (keys.Count == 0)
            return;

        var db = _redis.GetDatabase();

        foreach (var key in keys)
        {
            var expired = key.ExpiresAt!.Value <= now;
            var alertType = expired ? AlertType.ApiKeyExpired : AlertType.ApiKeyExpiring;
            var state = expired ? "expired" : "expiring";

            // Only alert once per key+state per TTL, across all instances.
            var firstTime = await db.StringSetAsync($"milvaion:apikey-alert:{key.Id}:{state}", "1", _dedupTtl, When.NotExists);

            if (!firstTime)
                continue;

            _alertNotifier.SendFireAndForget(alertType, BuildPayload(key, expired));
        }
    }

    private static AlertPayload BuildPayload(MilvaionApiKey key, bool expired) => new()
    {
        Title = expired ? "API key expired" : "API key expiring soon",
        Message = expired ? $"API key '{key.Name}' expired on {key.ExpiresAt:yyyy-MM-dd}." : $"API key '{key.Name}' expires on {key.ExpiresAt:yyyy-MM-dd}.",
        Severity = expired ? AlertSeverity.Warning : AlertSeverity.Info,
        Source = "ApiKeyExpiryMonitor",
        ActionLink = "/api-keys",
        AdditionalData = new { key.Id, key.Name, key.ExpiresAt }
    };
}
