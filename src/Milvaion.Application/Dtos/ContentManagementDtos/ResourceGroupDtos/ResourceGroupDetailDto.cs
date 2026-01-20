using Milvaion.Domain.ContentManagement;
using Milvasoft.Attributes.Annotations;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.ContentManagementDtos.ResourceGroupDtos;

/// <summary>
/// Response of namespace detail.
/// </summary>
public class ResourceGroupDetailDto : MilvaionBaseDto<int>
{
    /// <summary>
    /// Unique slug of namespace.
    /// </summary>
    public string Slug { get; set; }

    /// <summary>
    /// Name of namespace.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Description of namespace.
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// Description of namespace.
    /// </summary>
    public NameIntNavigationDto Namespace { get; set; }

    /// <summary>
    /// Information about record audit.
    /// </summary>
    public AuditDto<int> AuditInfo { get; set; }

    /// <summary>
    /// Projection expression for mapping ResourceGroup entity to ResourceGroupDetailDto.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<ResourceGroup, ResourceGroupDetailDto>> Projection { get; } = n => new ResourceGroupDetailDto
    {
        Id = n.Id,
        Slug = n.Slug,
        Name = n.Name,
        Description = n.Description,
        Namespace = new NameIntNavigationDto
        {
            Id = n.Namespace.Id,
            Name = n.Namespace.Name
        },
        AuditInfo = new AuditDto<int>(n)
    };
}
