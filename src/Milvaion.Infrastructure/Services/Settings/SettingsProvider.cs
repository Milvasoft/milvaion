using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces;
using Milvaion.Domain;
using Milvaion.Domain.JsonModels;
using StackExchange.Redis;

namespace Milvaion.Infrastructure.Services.Settings;

/// <summary>
/// In-memory settings cache backed by the single <see cref="AppSetting"/> row, kept in sync
/// across instances with Redis pub/sub.
///
/// Registered once and exposed both as <see cref="ISettingsProvider"/> and as an
/// <see cref="IHostedService"/>: on startup it loads the row (seeding defaults if the table is
/// empty) and subscribes to an invalidation channel. On update it writes the row, swaps its
/// cached copy and publishes to that channel, so every instance - including this one - reloads
/// and a runtime change propagates immediately without a restart.
/// </summary>
/// <inheritdoc cref="SettingsProvider"/>
public class SettingsProvider(IServiceScopeFactory scopeFactory, IConnectionMultiplexer redis, ILogger<SettingsProvider> logger, IOptions<AlertingOptions> alertingOptions) : ISettingsProvider, IHostedService
{
    /// <summary>
    /// Pub/sub channel used to tell every instance that the settings row changed. A literal name is fine: the message carries no data, it is only a "reload now" signal.
    /// </summary>
    private const string _invalidationChannel = "milvaion:settings:invalidated";
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IConnectionMultiplexer _redis = redis;
    private readonly ILogger<SettingsProvider> _logger = logger;
    private readonly AlertingOptions _alertingOptions = alertingOptions.Value;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile AppSettingsDocument _current;

    /// <inheritdoc/>
    public AppSettingsDocument Current => _current ?? CreateDefault();

    /// <inheritdoc/>
    public async Task<AppSettingsDocument> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_current is not null)
            return _current;

        await LoadAsync(cancellationToken);

        return _current;
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(AppSettingsDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMilvaionRepositoryBase<AppSetting>>();

        var row = await repo.GetFirstOrDefaultAsync(cancellationToken: cancellationToken);

        if (row is null)
        {
            row = new AppSetting { Document = document };
            await repo.AddAsync(row, cancellationToken);
        }
        else
        {
            row.Document = document;
        }

        await repo.UpdateAsync(row, cancellationToken);

        // Swap our own cache first, then tell the other instances to reload theirs.
        _current = document;

        try
        {
            await _redis.GetSubscriber().PublishAsync(RedisChannel.Literal(_invalidationChannel), "reload");
        }
        catch (Exception ex)
        {
            // A failed publish only means peers refresh a little later (on their next restart); it must not fail the update the admin just made.
            _logger.LogWarning(ex, "Failed to publish settings invalidation to Redis.");
        }
    }

    /// <summary>
    /// Loads the row into the cache, seeding defaults on first run. Serialised so a burst of
    /// concurrent readers on a cold cache performs a single database round-trip.
    /// </summary>
    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        await _loadLock.WaitAsync(cancellationToken);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMilvaionRepositoryBase<AppSetting>>();

            var row = await repo.GetFirstOrDefaultAsync(cancellationToken: cancellationToken);

            if (row is null)
            {
                var seeded = CreateDefault();

                await repo.AddAsync(new AppSetting { Document = seeded }, cancellationToken);

                _current = seeded;
            }
            else
            {
                var document = row.Document ?? CreateDefault();

                if (document.Notifications?.Rules is not { Count: > 0 })
                    document.Notifications = CreateDefault().Notifications;

                _current = document;
            }
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>
    /// The shipped defaults, seeded into the row on first run so there is always a known-good
    /// baseline: the branding the frontend falls back to, plus one notification rule per alert
    /// type taken from the appsettings alerting config (or enabled-on-the-default-channel when
    /// appsettings says nothing about that alert).
    /// </summary>
    private AppSettingsDocument CreateDefault()
    {
        var document = new AppSettingsDocument
        {
            Branding = new BrandingSettings
            {
                Title = "Milvaion",
                Subtitle = "Job Scheduler & Workflow Engine"
            }
        };

        var defaultChannel = string.IsNullOrWhiteSpace(_alertingOptions?.DefaultChannel) ? nameof(AlertChannelType.InternalNotification) : _alertingOptions.DefaultChannel;

        foreach (var alertType in Enum.GetValues<AlertType>())
        {
            if (alertType == AlertType.All)
                continue;

            bool enabled;
            List<string> channels;

            if (_alertingOptions?.Alerts != null && _alertingOptions.Alerts.TryGetValue(alertType, out var config))
            {
                enabled = config.Enabled;
                channels = config.Routes is { Count: > 0 } ? [.. config.Routes] : [defaultChannel];
            }
            else
            {
                enabled = true;
                channels = [defaultChannel];
            }

            document.Notifications.Rules.Add(new NotificationRule
            {
                AlertType = alertType,
                Enabled = enabled,
                Channels = channels
            });
        }

        return document;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await LoadAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Don't block startup on settings: fall back to defaults and let a later read retry.
            _logger.LogError(ex, "Failed to load application settings on startup; using defaults.");
            _current ??= CreateDefault();
        }

        try
        {
            await _redis.GetSubscriber().SubscribeAsync(RedisChannel.Literal(_invalidationChannel), (_, _) =>
            {
                // Fire-and-forget: the subscription callback is synchronous, so reload off it.
                _ = ReloadFromInvalidationAsync();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to subscribe to settings invalidation channel.");
        }
    }

    private async Task ReloadFromInvalidationAsync()
    {
        try
        {
            await LoadAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reload settings after an invalidation message.");
        }
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
