namespace Milvaion.Application.Utils.Models;

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
public class UISeedModel
{
    public List<MenuGroupModel> MenuGroups { get; set; }
    public List<MenuItemModel> MenuItems { get; set; }
    public List<PageModel> Pages { get; set; }
}

public class MenuGroupModel
{
    public int Id { get; set; }
    public int Order { get; set; }
    public List<TranslationModel> Translations { get; set; }
}

public class MenuItemModel
{
    public int Id { get; set; }
    public int Order { get; set; }
    public int GroupId { get; set; }
    public string ParentId { get; set; }
    public string Url { get; set; }
    public string PageName { get; set; }
    public List<string> PermissionOrGroupNames { get; set; }
    public List<TranslationModel> Translations { get; set; }
    public List<MenuItemModel> Children { get; set; }
}

public class PageModel
{
    public int Id { get; set; }
    public string Name { get; set; }
    public bool HasCreate { get; set; }
    public bool HasEdit { get; set; }
    public bool HasDetail { get; set; }
    public bool HasDelete { get; set; }
    public List<string> CreatePermissions { get; set; }
    public List<string> EditPermissions { get; set; }
    public List<string> DetailPermissions { get; set; }
    public List<string> DeletePermissions { get; set; }
    public List<PageActionModel> AdditionalActions { get; set; }
    public string CreationDate { get; set; }
    public string CreatorUserName { get; set; }
}

public class PageActionModel
{
    public int Id { get; set; }
    public string ActionName { get; set; }
    public List<string> Permissions { get; set; }
    public List<TranslationModel> Translations { get; set; }
    public int PageId { get; set; }
}

public class TranslationModel
{
    public int LanguageId { get; set; }
    public string Name { get; set; }
}
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
