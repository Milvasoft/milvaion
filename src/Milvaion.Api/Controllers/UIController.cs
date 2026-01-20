using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Milvaion.Application.Dtos.UIDtos;
using Milvaion.Application.Dtos.UIDtos.MenuItemDtos;
using Milvaion.Application.Dtos.UIDtos.PageDtos;
using Milvaion.Application.Features.Pages.GetPageAccessibilityForCurrentUser;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.Attributes;
using Milvaion.Application.Utils.Constants;
using Milvasoft.Components.Rest.MilvaResponse;

namespace Milvaion.Api.Controllers;

/// <summary>
/// Frontend related endpoints.
/// </summary>
[Auth]
[ApiController]
[Route(GlobalConstant.FullRoute)]
[ApiVersion(GlobalConstant.CurrentApiVersion)]
[ApiExplorerSettings(GroupName = "v1.0")]
public class UIController(IMediator mediator, IUIService uiService) : ControllerBase
{

    /// <summary>
    /// Gets accessible menu items.
    /// </summary>
    /// <returns></returns>
    [HttpGet("menuItems")]
    public Task<Response<List<MenuItemDto>>> GetAccessibleMenuItemsAsync(CancellationToken cancellation) => uiService.GetAccessibleMenuItemsForCurrentUserAsync(cancellation);

    /// <summary>
    /// Gets page information of current user.
    /// </summary>
    /// <returns></returns>
    [HttpGet("pages/page")]
    public Task<Response<PageDto>> GetPageAccessibilityForCurrentUserAsync([FromQuery] GetPageAccessibilityForCurrentUserQuery reqeust, CancellationToken cancellation) => mediator.Send(reqeust, cancellation);

    /// <summary>
    /// Gets page information of current user.
    /// </summary>
    /// <returns></returns>
    [HttpGet("pages")]
    public Task<Response<List<PageDto>>> GetPageAccessibilityForCurrentUserAsync(CancellationToken cancellation) => uiService.GetCurrentUserPagesAccessibilityAsync(cancellation);

    /// <summary>
    /// Gets localized contents related with UI.
    /// </summary>
    /// <returns></returns>
    [HttpGet("localizedContents")]
    [AllowAnonymous]
    public Response<List<LocalizedContentDto>> GetLocalizedContents() => uiService.GetLocalizedContents();
}
