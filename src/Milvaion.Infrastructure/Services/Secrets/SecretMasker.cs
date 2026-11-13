using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces.Secrets;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Milvaion.Infrastructure.Services.Secrets;

/// <summary>
/// Replaces recently resolved secret values with their references in text sent back by workers.
/// </summary>
/// <remarks>
/// Values live only in this process's memory and are forgotten after
/// <see cref="SecretsOptions.MaskRetentionMinutes"/> without use. Both the raw value and its JSON-escaped form
/// are matched, because a value echoed inside a JSON document (dead-lettered job data, log data) is escaped.
/// </remarks>
public class SecretMasker(IOptionsMonitor<SecretsOptions> options, TimeProvider timeProvider = null) : ISecretMasker
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (string Reference, DateTimeOffset LastSeen)> _values = new(StringComparer.Ordinal);
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    /// <inheritdoc/>
    public void Register(string reference, string value)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(reference))
            return;

        var minimum = Math.Max(1, options.CurrentValue.MinimumMaskLength);

        if (value.Length < minimum)
            return;

        var now = _timeProvider.GetUtcNow();

        _values[value] = (reference, now);

        // Serializers escape differently: most write \" and \\, System.Text.Json's default encoder writes " and
        // also escapes <, >, & and non-ASCII. Both forms are remembered.
        foreach (var escaped in new[] { SecretResolver.EscapeForJsonString(value), JsonEncodedText.Encode(value).ToString() })
        {
            if (escaped != value)
                _values[escaped] = (reference, now);
        }
    }

    /// <inheritdoc/>
    public string Mask(string text)
    {
        if (string.IsNullOrEmpty(text) || _values.IsEmpty)
            return text;

        PruneIfDue();

        // Longest first, so a value that contains another value is replaced as a whole.
        foreach (var (value, entry) in _values.OrderByDescending(v => v.Key.Length))
        {
            if (text.Contains(value, StringComparison.Ordinal))
                text = text.Replace(value, entry.Reference, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>
    /// Number of remembered values (raw and escaped forms both count).
    /// </summary>
    public int Count => _values.Count;

    private void PruneIfDue()
    {
        var now = _timeProvider.GetUtcNow();

        if (now - _lastPrune < TimeSpan.FromMinutes(1))
            return;

        _lastPrune = now;

        var cutoff = now.AddMinutes(-Math.Max(1, options.CurrentValue.MaskRetentionMinutes));

        foreach (var (value, entry) in _values)
        {
            if (entry.LastSeen < cutoff)
                _values.TryRemove(value, out _);
        }
    }
}
