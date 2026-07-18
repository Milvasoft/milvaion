using Milvaion.Api.Utils;

namespace Milvaion.Api.Services;

/// <summary>
/// Issues signed api keys using the configured signing secret.
/// </summary>
/// <param name="milvaionConfig"></param>
public class ApiKeyGenerator(MilvaionConfig milvaionConfig) : IApiKeyGenerator
{
    private readonly MilvaionConfig _milvaionConfig = milvaionConfig;

    /// <inheritdoc/>
    public int CurrentKeyVersion => _milvaionConfig.ApiKey.Version;

    /// <inheritdoc/>
    public string Generate(int apiKeyId, DateTime? expiresAt) => KeyHelper.GenerateApiKey(_milvaionConfig.ApiKey.SecretBytes, apiKeyId, _milvaionConfig.ApiKey.Version, expiresAt);

    /// <inheritdoc/>
    public string Mask(string key) => KeyHelper.Mask(key);
}
