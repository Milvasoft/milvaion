using Milvaion.Domain.ContentManagement;
using Milvasoft.Attributes.Annotations;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.ContentManagementDtos.ResourceGroupDtos;

/// <summary>
/// Response of resource group detail.
/// </summary>
[Translate]
public class ResourceGroupListDto : MilvaionBaseDto<int>
{
    /// <summary>
    /// Unique slug of resource group.
    /// </summary>
    [Info(MessageKey.SlugExplaniton)]
    public string Slug { get; set; }

    /// <summary>
    /// Name of resource group.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Description of resource group.
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// Description of namespace.
    /// </summary>
    [Filterable(false)]
    [DisplayFormat("{namespace.name}")]
    [ClientDefaultValue(MessageConstant.Hypen)]
    public NameIntNavigationDto Namespace { get; set; }

    /// <summary>
    /// Projection expression for mapping ResourceGroup entity to ResourceGroupListDto.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<ResourceGroup, ResourceGroupListDto>> Projection { get; } = n => new ResourceGroupListDto
    {
        Id = n.Id,
        Slug = n.Slug,
        Name = n.Name,
        Description = n.Description,
        Namespace = new NameIntNavigationDto
        {
            Id = n.Namespace.Id,
            Name = n.Namespace.Name
        }
    };
}
