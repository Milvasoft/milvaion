namespace Milvaion.Application.Interfaces.Secrets;

/// <summary>
/// A source of secret values that job data can reference with <c>{{secret:name}}</c>.
/// </summary>
/// <remarks>
/// Implementations are registered in dependency injection and consulted in the order configured in
/// <c>MilvaionConfig:Secrets:Providers</c>. A reference can also name one provider explicitly with
/// <c>{{secret:provider:name}}</c>, for example <c>{{secret:vault:smtp#password}}</c>.
/// </remarks>
public interface ISecretProvider
{
    /// <summary>
    /// Short, case-insensitive name of the provider, used in configuration and in explicit references
    /// (for example <c>env</c>, <c>config</c>, <c>vault</c>, <c>azurekeyvault</c>).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Reads one secret.
    /// </summary>
    /// <param name="name">The secret name as written in the reference, without the provider prefix.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The value, or <c>null</c> when this provider has no secret with that name.</returns>
    /// <remarks>
    /// Return <c>null</c> for "not found" so the next provider can be tried. Throw only for real failures
    /// (unreachable server, access denied); the message of that exception must not contain the value.
    /// </remarks>
    Task<string> GetSecretAsync(string name, CancellationToken cancellationToken = default);
}
