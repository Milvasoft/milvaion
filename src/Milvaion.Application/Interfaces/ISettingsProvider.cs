namespace Milvaion.Application.Interfaces;

/// <summary>
/// Provides the application's runtime settings, cached in memory and kept in sync across
/// instances via Redis pub/sub. Read <see cref="Current"/> on hot paths - it never hits the
/// database. An update persists the row, refreshes this instance's cache and tells the other
/// instances to refresh theirs, so a change takes effect at runtime without a restart.
/// </summary>
public interface ISettingsProvider
{
    /// <summary>
    /// The current settings from the in-memory cache. Never null; before the first load it
    /// returns defaults.
    /// </summary>
    AppSettingsDocument Current { get; }

    /// <summary>
    /// Ensures the settings are loaded (seeding defaults on first run) and returns them.
    /// </summary>
    Task<AppSettingsDocument> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the settings, refreshes the local cache and notifies the other instances to
    /// refresh theirs.
    /// </summary>
    Task UpdateAsync(AppSettingsDocument document, CancellationToken cancellationToken = default);
}
