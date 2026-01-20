using Microsoft.AspNetCore.Http;
using Milvaion.Application.Dtos.UIDtos;
using Milvaion.Application.Dtos.UIDtos.MenuItemDtos;
using Milvaion.Application.Dtos.UIDtos.PageDtos;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.Extensions;
using Milvaion.Domain;
using Milvaion.Domain.UI;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions.Localization;
using Milvasoft.Core.Helpers;
using Milvasoft.Core.MultiLanguage.Manager;

namespace Milvaion.Infrastructure.Services;

/// <summary>
/// Service for UI operations.
/// </summary>
/// <param name="multiLanguageManager"></param>
/// <param name="menuItemRepository"></param>
/// <param name="pageRepository"></param>
/// <param name="userRepository"></param>
/// <param name="httpContextAccessor"></param>
/// <param name="milvaLocalizer"></param>
public class UIService(IMultiLanguageManager multiLanguageManager,
                       IMilvaionRepositoryBase<MenuItem> menuItemRepository,
                       IMilvaionRepositoryBase<Page> pageRepository,
                       IMilvaionRepositoryBase<User> userRepository,
                       IHttpContextAccessor httpContextAccessor,
                       IMilvaLocalizer milvaLocalizer) : IUIService
{
    private readonly IMultiLanguageManager _multiLanguageManager = multiLanguageManager;
    private readonly IMilvaionRepositoryBase<MenuItem> _menuItemRepository = menuItemRepository;
    private readonly IMilvaionRepositoryBase<Page> _pageRepository = pageRepository;
    private readonly IMilvaionRepositoryBase<User> _userRepository = userRepository;
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly IMilvaLocalizer _milvaLocalizer = milvaLocalizer;

    #region MenuItem

    /// <summary>
    /// Gets accessible menu items according to <paramref name="userPermissions"/>.
    /// </summary>
    /// <param name="userPermissions"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<List<MenuItemDto>> GetAccessibleMenuItemsAsync(IEnumerable<Permission> userPermissions, CancellationToken cancellationToken = default)
    {
        var menuItems = await _menuItemRepository.GetAllAsync(projection: MenuItem.Projections.AccessibleMenuItems, cancellationToken: cancellationToken);

        var hierarchedMenuItems = BuildHierarchy(menuItems.Where(mi => mi.PermissionOrGroupNames.Exists(p => userPermissions.Any(up => up.PermissionGroup == p || up.FormatPermissionAndGroup() == p))));

        return [.. hierarchedMenuItems.Select(MenuItemDto.Projection(_multiLanguageManager))];
    }

    /// <summary>
    /// Gets accessible menu items according to current user permissions.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<Response<List<MenuItemDto>>> GetAccessibleMenuItemsForCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        var menuItems = await _menuItemRepository.GetAllAsync(projection: MenuItem.Projections.AccessibleMenuItems, cancellationToken: cancellationToken);

        var userName = _httpContextAccessor.HttpContext.User.Identity.Name;

        var user = await _userRepository.GetFirstOrDefaultAsync(i => i.UserName == userName, User.Projections.Permissions, cancellationToken: cancellationToken);

        var userPermissions = user.RoleRelations.SelectMany(i => i.Role.RolePermissionRelations.Select(i => i.Permission));

        var hierarchedMenuItems = BuildHierarchy(menuItems.Where(mi => mi.PermissionOrGroupNames.Exists(p => userPermissions.Any(up => up.PermissionGroup == p || up.FormatPermissionAndGroup() == p))));

        return Response<List<MenuItemDto>>.Success([.. hierarchedMenuItems.Select(MenuItemDto.Projection(_multiLanguageManager))]);
    }

    #endregion

    #region Page

    /// <summary>
    /// Gets page information by <paramref name="pageName"/>. 
    /// </summary>
    /// <param name="pageName"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<PageDto> GetCurrentUserPageAccessibilityAsync(string pageName, CancellationToken cancellationToken = default)
    {
        var page = await _pageRepository.GetFirstOrDefaultAsync(p => p.Name == pageName, Page.Projections.PageInfo, cancellationToken: cancellationToken);

        if (page == null)
            return null;

        var currentUserPermissions = _httpContextAccessor.HttpContext.GetCurrentUserPermissions();

        page.AdditionalActions = [.. page.AdditionalActions.Where(pa => currentUserPermissions.Any(pa.Permissions.Contains))];

        var pageDto = PageDto.Projection(_multiLanguageManager).Invoke(page);

        pageDto.UserCanCreate = pageDto.HasCreate && currentUserPermissions.Any(cup => page.CreatePermissions?.Contains(cup) ?? false);
        pageDto.UserCanDetail = pageDto.HasDetail && currentUserPermissions.Any(cup => page.DetailPermissions?.Contains(cup) ?? false);
        pageDto.UserCanEdit = pageDto.HasEdit && currentUserPermissions.Any(cup => page.EditPermissions?.Contains(cup) ?? false);
        pageDto.UserCanDelete = pageDto.HasDelete && currentUserPermissions.Any(cup => page.DeletePermissions?.Contains(cup) ?? false);

        return pageDto;
    }

    /// <summary>
    /// Gets pages information according to <paramref name="userPermissions"/>. 
    /// </summary>
    /// <param name="userPermissions"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<List<PageDto>> GetPagesAccessibilityAsync(IEnumerable<string> userPermissions, CancellationToken cancellationToken = default)
    {
        var pages = await _pageRepository.GetAllAsync(projection: Page.Projections.PageInfo, cancellationToken: cancellationToken);

        if (pages.IsNullOrEmpty())
            return [];

        var results = new List<PageDto>();

        foreach (var page in pages)
        {
            page.AdditionalActions = [.. page.AdditionalActions.Where(pa => userPermissions.Any(pa.Permissions.Contains))];

            var pageDto = PageDto.Projection(_multiLanguageManager).Invoke(page);

            pageDto.UserCanCreate = pageDto.HasCreate && userPermissions.Any(cup => page.CreatePermissions?.Contains(cup) ?? false);
            pageDto.UserCanDetail = pageDto.HasDetail && userPermissions.Any(cup => page.DetailPermissions?.Contains(cup) ?? false);
            pageDto.UserCanEdit = pageDto.HasEdit && userPermissions.Any(cup => page.EditPermissions?.Contains(cup) ?? false);
            pageDto.UserCanDelete = pageDto.HasDelete && userPermissions.Any(cup => page.DeletePermissions?.Contains(cup) ?? false);

            pageDto.LocalizedName = _milvaLocalizer[$"UI.{page.Name}"];

            results.Add(pageDto);
        }

        return results;
    }

    /// <summary>
    /// Gets pages information for current user. 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<Response<List<PageDto>>> GetCurrentUserPagesAccessibilityAsync(CancellationToken cancellationToken = default)
    {
        var currentUserPermissions = _httpContextAccessor.HttpContext.GetCurrentUserPermissions();

        return Response<List<PageDto>>.Success(await GetPagesAccessibilityAsync(currentUserPermissions, cancellationToken));
    }

    #endregion

    /// <summary>
    /// Gets localized contents related to UI.
    /// </summary>
    /// <returns></returns>
    public Response<List<LocalizedContentDto>> GetLocalizedContents()
    {
        var localizedValues = _milvaLocalizer.GetAllStrings(false);

        var uiLocalizedValues = localizedValues.Where(lv => lv.Key.StartsWith("UI.")).Select(lv => new LocalizedContentDto
        {
            Key = lv.Key,
            Value = lv.Value
        });

        return Response<List<LocalizedContentDto>>.Success([.. uiLocalizedValues]);
    }

    /// <summary>
    /// Builds parent-child hierarchy for menu items.
    /// </summary>
    /// <param name="menuItems"></param>
    /// <returns></returns>
    private static List<MenuItem> BuildHierarchy(IEnumerable<MenuItem> menuItems)
    {
        if (menuItems.IsNullOrEmpty())
            return [];

        // Group by parent id
        var lookup = menuItems.ToLookup(x => x.ParentId);

        // Assign childrens to their parent
        foreach (var menuItem in menuItems.Where(menuItem => lookup.Contains(menuItem.Id)))
        {
            var childrens = lookup[menuItem.Id];

            foreach (var children in childrens)
                children.Parent = null;

            menuItem.Childrens = [];
            menuItem.Childrens.AddRange(lookup[menuItem.Id]);
        }

        // Get the parent items
        var result = menuItems.Where(c => c.ParentId == null).ToList();

        return result;
    }
}
