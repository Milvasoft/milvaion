using Milvaion.Domain.ContentManagement;
using Milvasoft.Attributes.Annotations;
using System.ComponentModel;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.ContentManagementDtos.ContentDtos;

/// <summary>
/// Content list grouped by key.
/// </summary>
[Translate]
public class GroupedContentListDto
{
    /// <summary>
    /// Grouped contents id list.
    /// </summary>
    [Filterable(false)]
    [Browsable(false)]
    public List<int> IdList { get; set; }

    /// <summary>
    /// Key alias for search without join. A combination of namespace slug, resource group slug and key. etc. "namespaceSlug.resourceGroupSlug.key"
    /// </summary>
    public string KeyAlias { get; set; }

    /// <summary>
    /// Key of content.
    /// </summary>
    public string Key { get; set; }

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
    public static Expression<Func<Content, GroupedContentListDto>> Projection { get; } = c => new GroupedContentListDto
    {
        Key = c.Key,
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
