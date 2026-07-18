using Milvasoft.Attributes.Annotations;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.ApiKeyDtos;

/// <summary>
/// Data transfer object for api key list.
/// </summary>
[Translate]
public class ApiKeyListDto : MilvaionBaseDto<int>
{
    /// <summary>
    /// Human readable name of the key.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Trailing characters of the key, for matching against a stored configuration value.
    /// </summary>
    public string MaskedKey { get; set; }

    /// <summary>
    /// UTC expiry of the key. Null means it does not expire.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// UTC time the key was revoked. Null means the key is still active.
    /// </summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// UTC time the key was last used to authenticate a request.
    /// </summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// Projection expression for mapping MilvaionApiKey entity to ApiKeyListDto.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<MilvaionApiKey, ApiKeyListDto>> Projection { get; } = a => new ApiKeyListDto
    {
        Id = a.Id,
        Name = a.Name,
        MaskedKey = a.MaskedKey,
        ExpiresAt = a.ExpiresAt,
        RevokedAt = a.RevokedAt,
        LastUsedAt = a.LastUsedAt
    };
}
