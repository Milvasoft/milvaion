using Milvaion.Domain.Enums;

namespace Milvaion.Domain.JsonModels;

/// <summary>
/// The application's runtime-tunable settings, stored as a single jsonb document.
///
/// Sectioned: each feature owns a section, so the document grows over time. Because the
/// column is jsonb and Npgsql (de)serializes the whole object, adding a field here never
/// requires a database migration.
///
/// Do NOT put here:
/// - bootstrap-critical values (DB / Redis / RabbitMQ connection strings) - the app needs
///   those before it can read this row, so they stay in appsettings;
/// - secrets (SMTP passwords, webhook URLs) - this document is surfaced to the settings UI;
/// - binary assets (logo, favicon) - those stay static, served from files.
/// </summary>
public class AppSettingsDocument
{
    /// <summary>
    /// Branding text shown in the browser tab, sidebar and login page. The logo and favicon
    /// are intentionally not here - they are static files and not runtime-configurable.
    /// </summary>
    public BrandingSettings Branding { get; set; } = new();

    /// <summary>
    /// Per-notification routing and on/off, managed at runtime. Mirrors the appsettings
    /// alerting config but lives here so channels and enablement can be switched without a
    /// restart. Seeded from appsettings on first run. Channel availability itself (whether
    /// Slack/Email/... are configured) stays in appsettings and is display-only.
    /// </summary>
    public NotificationSettings Notifications { get; set; } = new();
}

/// <summary>
/// Runtime notification settings - one rule per alert type.
/// </summary>
public class NotificationSettings
{
    /// <summary>
    /// One entry per <see cref="AlertType"/>: whether it fires and which channels it routes to.
    /// </summary>
    public List<NotificationRule> Rules { get; set; } = [];
}

/// <summary>
/// Runtime configuration for a single alert type.
/// </summary>
public class NotificationRule
{
    /// <summary>The alert this rule applies to.</summary>
    public AlertType AlertType { get; set; }

    /// <summary>Whether this alert is sent at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Channel names this alert routes to (e.g. "Email", "Slack", "InternalNotification").
    /// Matches the appsettings <c>Routes</c> values.
    /// </summary>
    public List<string> Channels { get; set; } = [];
}

/// <summary>
/// Branding text. Only text is runtime-configurable; images stay static.
/// </summary>
public class BrandingSettings
{
    /// <summary>
    /// Application title shown in the browser tab, the sidebar and the login page. When empty,
    /// the frontend falls back to its shipped default ("Milvaion").
    /// </summary>
    public string Title { get; set; }

    /// <summary>
    /// Short tagline shown under the title (e.g. on the login page). Optional; falls back to
    /// the shipped default when empty.
    /// </summary>
    public string Subtitle { get; set; }
}
