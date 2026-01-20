using Milvasoft.Attributes.Annotations;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.PermissionDtos;

/// <summary>
/// Data transfer object for permission list.
/// </summary>
[Translate]
public class PermissionListDto : MilvaionBaseDto<int>
{
    /// <summary>
    /// Permission name. (e.g. List, Update)
    /// </summary>
    [Filterable(false)]
    public string Name { get; set; }

    /// <summary>
    /// Permission description. (e.g. User list permission. ) 
    /// </summary>
    [Filterable(false)]
    public string Description { get; set; }

    /// <summary>
    /// Permission group. (e.g. UserManagement) 
    /// </summary>
    [Filterable(false)]
    public string PermissionGroup { get; set; }

    /// <summary>
    /// Permission group description. (e.g. User management permissions)
    /// </summary>
    [Filterable(false)]
    public string PermissionGroupDescription { get; set; }

    /// <summary>
    /// Projection expression for mapping Permission entity to PermissionListDto.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<Permission, PermissionListDto>> Projection { get; } = p => new PermissionListDto
    {
        Id = p.Id,
        Name = p.Name,
        Description = p.Description,
        PermissionGroup = p.PermissionGroup,
        PermissionGroupDescription = p.PermissionGroupDescription
    };

}
