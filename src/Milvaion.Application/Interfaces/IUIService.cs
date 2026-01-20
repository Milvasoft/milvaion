using Milvaion.Application.Dtos.UIDtos;
using Milvaion.Application.Dtos.UIDtos.MenuItemDtos;
using Milvaion.Application.Dtos.UIDtos.PageDtos;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Interfaces;

/// <summary>
/// Service for UI operations.
/// </summary>
public interface IUIService : IInterceptable
{
    /// <summary>
    /// Gets accessible menu items according to <paramref name="userPermissions"/>.
    /// </summary>
    /// <param name="userPermissions"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<List<MenuItemDto>> GetAccessibleMenuItemsAsync(IEnumerable<Permission> userPermissions, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets accessible menu items according to current user permissions.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<Response<List<MenuItemDto>>> GetAccessibleMenuItemsForCurrentUserAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets page information by <paramref name="pageName"/>.
    /// </summary>
    /// <param name="pageName"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<PageDto> GetCurrentUserPageAccessibilityAsync(string pageName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets pages information.
    /// </summary>
    /// <param name="userPermissions"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<List<PageDto>> GetPagesAccessibilityAsync(IEnumerable<string> userPermissions, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets pages information for current user.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<Response<List<PageDto>>> GetCurrentUserPagesAccessibilityAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets localized contents related to UI.
    /// </summary>
    /// <returns></returns>
    Response<List<LocalizedContentDto>> GetLocalizedContents();
}
