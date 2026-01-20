using Milvaion.Domain.ContentManagement;
using Milvasoft.Attributes.Annotations;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.ContentManagementDtos.NamespaceDtos;

/// <summary>
/// Response of namespace detail.
/// </summary>
[Translate]
public class NamespaceListDto : MilvaionBaseDto<int>
{
    /// <summary>
    /// Unique slug of namespace.
    /// </summary>
    [Info(MessageKey.SlugExplaniton)]
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
    /// Projection expression for mapping Namespace entity to GetNamespaceListResponse.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<Namespace, NamespaceListDto>> Projection { get; } = n => new NamespaceListDto
    {
        Id = n.Id,
        Slug = n.Slug,
        Name = n.Name,
        Description = n.Description
    };
}
