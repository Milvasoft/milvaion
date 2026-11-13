namespace Milvaion.Application.Interfaces.Secrets;

/// <summary>
/// Puts secret references back in place of secret values in text that comes back from workers.
/// </summary>
/// <remarks>
/// A worker receives the resolved job data, and nothing stops it from echoing a value into a log line,
/// an exception message or the job data of a dead-lettered message. Before the API stores any of those,
/// it passes them through <see cref="Mask"/>, which replaces every value resolved recently by this
/// API instance with the reference it came from (<c>{{secret:name}}</c>).
/// </remarks>
public interface ISecretMasker
{
    /// <summary>
    /// Remembers that <paramref name="value"/> was resolved from <paramref name="reference"/>.
    /// </summary>
    /// <param name="reference">The full reference text, for example <c>{{secret:smtp-password}}</c>.</param>
    /// <param name="value">The resolved value.</param>
    void Register(string reference, string value);

    /// <summary>
    /// Returns <paramref name="text"/> with every remembered value replaced by its reference.
    /// </summary>
    string Mask(string text);
}
