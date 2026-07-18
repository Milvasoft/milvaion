namespace Milvaion.Application.Interfaces;

/// <summary>
/// Issues signed api keys for <see cref="MilvaionApiKey"/> records.
/// </summary>
/// <remarks>
/// The signing implementation lives in the API layer because that is where the signing secret is configured.
/// This abstraction keeps the application layer free of that dependency.
/// </remarks>
public interface IApiKeyGenerator
{
    /// <summary>
    /// Version of the signing secret currently in use. Stamped onto every newly issued key.
    /// </summary>
    int CurrentKeyVersion { get; }

    /// <summary>
    /// Generates a signed key for the given persisted api key record.
    /// </summary>
    /// <param name="apiKeyId">Id of the persisted <see cref="MilvaionApiKey"/> record.</param>
    /// <param name="expiresAt">UTC expiry, or null for a key that never expires.</param>
    /// <returns>The key. This is the only time it exists in plain form - it is never persisted.</returns>
    string Generate(int apiKeyId, DateTime? expiresAt);

    /// <summary>
    /// Masks a generated key for display and storage.
    /// </summary>
    string Mask(string key);
}
