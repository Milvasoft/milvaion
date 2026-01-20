using Milvaion.Application.Dtos.UIDtos.MenuItemDtos;
using Milvaion.Application.Dtos.UIDtos.PageDtos;
using Milvasoft.Attributes.Annotations;
using Milvasoft.Identity.Concrete;
using System.ComponentModel;

namespace Milvaion.Application.Dtos.AccountDtos;

/// <summary>
/// Response object for login operation.
/// </summary>
[Translate]
[ExcludeFromMetadata]
public class LoginResponseDto : MilvaionBaseDto<int>
{
    /// <summary>
    /// Type of user.
    /// </summary>
    [DisplayFormat("{userTypeDescription}")]
    public UserType UserType { get; set; }

    /// <summary>
    /// User type description.
    /// </summary>
    [Filterable(false)]
    [Browsable(false)]
    [LinkedWith<EnumFormatter<UserType>>(nameof(UserType), $"{EnumFormatter<UserType>.FormatterName}.{nameof(UserType)}")]
    public string UserTypeDescription { get; set; }

    /// <summary>
    /// Token information of the user.
    /// </summary>
    public MilvaToken Token { get; set; }

    /// <summary>
    /// Accessible menu items of the user.
    /// </summary>
    public List<MenuItemDto> AccessibleMenuItems { get; set; }

    /// <summary>
    /// Page informations.
    /// </summary>
    public List<PageDto> PageInformations { get; set; }
}
