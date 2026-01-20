using Milvaion.Domain.ContentManagement;
using Milvasoft.Attributes.Annotations;
using System.ComponentModel;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.ContentManagementDtos.ContentDtos;

/// <summary>
/// Response of namespace detail.
/// </summary>
[Translate]
public class ContentListDto : MilvaionBaseDto<int>
{
    /// <summary>
    /// Key alias for search without join. A combination of namespace slug, resource group slug and key. etc. "namespaceSlug.resourceGroupSlug.key"
    /// </summary>
    public string KeyAlias { get; set; }

    /// <summary>
    /// Key of content.
    /// </summary>
    public string Key { get; set; }

    /// <summary>
    /// Value of content.
    /// </summary>
    public string Value { get; set; }

    /// <summary>
    /// LanguageId of content.
    /// </summary>
    [DisplayFormat("{languageName}")]
    public int LanguageId { get; set; }

    /// <summary>
    /// LanguageId of content.
    /// </summary>
    [Browsable(false)]
    [Filterable(false)]
    [LinkedWith<LanguageIdNameFormatter>(nameof(LanguageId), LanguageIdNameFormatter.FormatterName)]
    [ClientDefaultValue(MessageConstant.QuestionMark)]
    public string LanguageName { get; set; }

    /// <summary>
    /// Slug for the namespace for search without join.
    /// </summary>
    public string NamespaceSlug { get; set; }

    /// <summary>
    /// Slug for the resource group for search without join.
    /// </summary>
    public string ResourceGroupSlug { get; set; }

    /// <summary>
    /// Id of the namespace this content belongs to.
    /// </summary>
    [Filterable(false)]
    [DisplayFormat("{namespace.name}")]
    [ClientDefaultValue(MessageConstant.Hypen)]
    public NameIntNavigationDto Namespace { get; set; }

    /// <summary>
    /// Id of the namespace this content belongs to.
    /// </summary>
    [Filterable(false)]
    [DisplayFormat("{resourceGroup.name}")]
    [ClientDefaultValue(MessageConstant.Hypen)]
    public NameIntNavigationDto ResourceGroup { get; set; }

    /// <summary>
    /// Projection expression for mapping Content entity to ContentListDto.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<Content, ContentListDto>> Projection { get; } = c => new ContentListDto
    {
        Id = c.Id,
        Key = c.Key,
        Value = c.Value,
        LanguageId = c.LanguageId,
        NamespaceSlug = c.NamespaceSlug,
        ResourceGroupSlug = c.ResourceGroupSlug,
        KeyAlias = c.KeyAlias,
        Namespace = new NameIntNavigationDto
        {
            Id = c.NamespaceId,
            Name = c.Namespace.Name
        },
        ResourceGroup = new NameIntNavigationDto
        {
            Id = c.ResourceGroupId,
            Name = c.ResourceGroup.Name
        }
    };
}
