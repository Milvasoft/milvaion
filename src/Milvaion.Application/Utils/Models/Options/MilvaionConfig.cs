namespace Milvaion.Application.Utils.Models.Options;

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
public class MilvaionConfig
{
    /// <summary>
    /// Prefix this application receives requests on (e.g. "/milvaion"), applied as the ASP.NET path base.
    /// Leave empty or null to keep the default root behaviour.
    ///
    /// This is the server's own view. Behind a reverse proxy that strips the prefix before forwarding, the
    /// application is reached at the root and this must stay empty - set <see cref="PublicBasePath"/> to the
    /// prefix the browser uses instead.
    /// </summary>
    public string BasePath { get; set; } = string.Empty;

    /// <summary>
    /// Prefix the browser addresses the application on, when it differs from <see cref="BasePath"/>.
    /// Defaults to <see cref="BasePath"/>, which is correct whenever the prefix is passed through untouched.
    ///
    /// Everything resolved client-side hangs off this: the asset URLs in index.html, the loader for lazily
    /// routed chunks, the PWA manifest scope, the router basename, and the base URL for API and SignalR calls.
    /// Those are all addressed by the browser through the proxy, so they need the public prefix even when the
    /// application itself never sees it.
    ///
    /// Set this only for a prefix-stripping proxy. Keeping the prefix end to end is simpler and leaves the two
    /// values equal.
    /// </summary>
    public string PublicBasePath { get; set; }

    /// <summary>
    /// The prefix to hand to the browser: <see cref="PublicBasePath"/> when configured, otherwise
    /// <see cref="BasePath"/>. Null and empty are treated alike, so an unset value falls through rather than
    /// forcing the UI to the root.
    /// </summary>
    public string EffectivePublicBasePath => string.IsNullOrWhiteSpace(PublicBasePath) ? BasePath : PublicBasePath;

    /// <summary>
    /// Api key signing and versioning settings.
    /// </summary>
    public ApiKeyOptions ApiKey { get; set; }

    public RedisOptions Redis { get; set; }
    public RabbitMQOptions RabbitMQ { get; set; }
    public JobDispatcherOptions JobDispatcher { get; set; }
    public ZombieOccurrenceDetectorOptions ZombieOccurrenceDetector { get; set; }
    public JobAutoDisableOptions JobAutoDisable { get; set; }
    public AlertingOptions Alerting { get; set; }

    /// <summary>
    /// External identity settings (OIDC and LDAP/AD). Off by default, so local username/password stays in effect until a provider is enabled.
    /// </summary>
    public AuthenticationOptions Authentication { get; set; } = new();
}

#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
