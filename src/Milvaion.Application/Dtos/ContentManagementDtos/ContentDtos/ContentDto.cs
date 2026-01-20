using Milvaion.Domain.ContentManagement;
using Milvasoft.Attributes.Annotations;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.ContentManagementDtos.ContentDtos;

/// <summary>
/// Response of namespace detail.
/// </summary>
[Translate]
public class ContentDto : MilvaionBaseDto<int>
{
    /// <summary>
    /// Key of content.
    /// </summary>
    [Filterable(false)]
    public string Key { get; set; }

    /// <summary>
    /// Value of content.
    /// </summary>
    [Filterable(false)]
    public string Value { get; set; }

    /// <summary>
    /// LanguageId of content.
    /// </summary>
    [JsonIgnore]
    public int LanguageId { get; set; }

    /// <summary>
    /// Slug for the namespace for search without join.
    /// </summary>
    [JsonIgnore]
    public string NamespaceSlug { get; set; }

    /// <summary>
    /// Slug for the resource group for search without join.
    /// </summary>
    [JsonIgnore]
    public string ResourceGroupSlug { get; set; }

    /// <summary>
    /// Key alias for search without join. A combination of namespace slug, resource group slug and key. etc. "namespaceSlug.resourceGroupSlug.key"
    /// </summary>
    [JsonIgnore]
    public string KeyAlias { get; set; }

    /// <summary>
    /// Content related medias.
    /// </summary>
    [Filterable(false)]
    public List<MediaDto> Medias { get; set; }

    /// <summary>
    /// Projection expression for mapping Content entity to ContentDto.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<Content, ContentDto>> Projection { get; } = c => new ContentDto
    {
        Id = c.Id,
        Key = c.Key,
        Value = c.Value,
        LanguageId = c.LanguageId,
        NamespaceSlug = c.NamespaceSlug,
        ResourceGroupSlug = c.ResourceGroupSlug,
        KeyAlias = c.KeyAlias,
        Medias = c.Medias.Select(m => new MediaDto
        {
            Id = m.Id,
            Type = m.Type,
            Value = m.Value
        }).ToList(),
    };
}
