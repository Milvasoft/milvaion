using Milvasoft.Attributes.Annotations;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.ApiKeyDtos;

/// <summary>
/// Data transfer object for api key details.
/// </summary>
/// <remarks>
/// Never contains the key itself. The key is returned exactly once, by the create endpoint.
/// </remarks>
[Translate]
[ExcludeFromMetadata]
public class ApiKeyDetailDto : MilvaionBaseDto<int>
{
    /// <summary>
    /// Human readable name of the key.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Description explaining what the key is used for.
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// Trailing characters of the key, for matching against a stored configuration value.
    /// </summary>
    public string MaskedKey { get; set; }

    /// <summary>
    /// Version of the signing secret this key was issued with.
    /// </summary>
    public int KeyVersion { get; set; }

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
    /// Permissions granted to the key.
    /// </summary>
    public List<NameIntNavigationDto> Permissions { get; set; }

    /// <summary>
    /// Information about record audit.
    /// </summary>
    public AuditDto<int> AuditInfo { get; set; }

    /// <summary>
    /// Projection expression for mapping MilvaionApiKey entity to ApiKeyDetailDto.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<MilvaionApiKey, ApiKeyDetailDto>> Projection { get; } = a => new ApiKeyDetailDto
    {
        Id = a.Id,
        Name = a.Name,
        Description = a.Description,
        MaskedKey = a.MaskedKey,
        KeyVersion = a.KeyVersion,
        ExpiresAt = a.ExpiresAt,
        RevokedAt = a.RevokedAt,
        LastUsedAt = a.LastUsedAt,
        Permissions = a.ApiKeyPermissionRelations.Select(p => new NameIntNavigationDto
        {
            Id = p.PermissionId,
            Name = p.Permission.Name,
        }).ToList(),
        AuditInfo = new AuditDto<int>(a)
    };
}
