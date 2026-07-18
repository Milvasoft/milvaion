using Milvasoft.Attributes.Annotations;

namespace Milvaion.Application.Dtos.ApiKeyDtos;

/// <summary>
/// Data transfer object returned when an api key is created.
/// </summary>
/// <remarks>
/// This is the only response in the system that ever carries <see cref="Key"/>. The key is not persisted, so
/// once this response is discarded it cannot be recovered and a new key must be created.
/// </remarks>
[ExcludeFromMetadata]
public class CreatedApiKeyDto
{
    /// <summary>
    /// Id of the created api key record.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Human readable name of the key.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// The generated key. Shown once, never again.
    /// </summary>
    public string Key { get; set; }

    /// <summary>
    /// UTC expiry of the key. Null means it does not expire.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }
}
