using Milvaion.Domain.ContentManagement;
using Milvasoft.Attributes.Annotations;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.ContentManagementDtos.NamespaceDtos;

/// <summary>
/// Response of namespace detail.
/// </summary>
public class NamespaceDetailDto : MilvaionBaseDto<int>
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
    /// Information about record audit.
    /// </summary>
    public AuditDto<int> AuditInfo { get; set; }

    /// <summary>
    /// Projection expression for mapping Namespace entity to GetNamespaceDetailResponse.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<Namespace, NamespaceDetailDto>> Projection { get; } = n => new NamespaceDetailDto
    {
        Id = n.Id,
        Slug = n.Slug,
        Name = n.Name,
        Description = n.Description,
        AuditInfo = new AuditDto<int>(n)
    };
}
