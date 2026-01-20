using Milvasoft.Attributes.Annotations;

namespace Milvaion.Application.Dtos.UIDtos.MenuItemDtos;

/// <summary>
/// Data transfer object for user list.
/// </summary>
[Translate]
[ExcludeFromMetadata]
public class MenuGroupDto : MilvaionBaseDto<int>
{
    /// <summary>
    /// Name of related domain.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Order of menu item.
    /// </summary>
    public int Order { get; set; }
}