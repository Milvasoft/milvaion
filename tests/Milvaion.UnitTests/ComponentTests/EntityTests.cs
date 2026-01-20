using FluentAssertions;
using Microsoft.EntityFrameworkCore.Query;
using Milvaion.Domain;
using Milvaion.Domain.ContentManagement;
using Milvaion.Domain.Enums;
using Milvaion.Domain.JsonModels;
using Milvaion.Domain.UI;
using System.Linq.Expressions;

namespace Milvaion.UnitTests.ComponentTests;

[Trait("Entity's Unit Tests", "Entity models property and method unit tests.")]
public class EntityTests
{
    [Fact]
    public void TableNames_ShouldHaveCorrectValues()
    {
        TableNames.Users.Should().Be("Users");
        TableNames.Roles.Should().Be("Roles");
        TableNames.UserRoleRelations.Should().Be("UserRoleRelations");
        TableNames.Permissions.Should().Be("Permissions");
        TableNames.RolePermissionRelations.Should().Be("RolePermissionRelations");
        TableNames.ActivityLogs.Should().Be("ActivityLogs");
        TableNames.ApiLogs.Should().Be("ApiLogs");
        TableNames.MethodLogs.Should().Be("MethodLogs");
        TableNames.UserSessions.Should().Be("UserSessions");
        TableNames.MenuItems.Should().Be("MenuItems");
        TableNames.MenuItemTranslations.Should().Be("MenuItemTranslations");
        TableNames.MenuGroups.Should().Be("MenuGroups");
        TableNames.MenuGroupTranslations.Should().Be("MenuGroupTranslations");
        TableNames.Pages.Should().Be("Pages");
        TableNames.PageActions.Should().Be("PageActions");
        TableNames.PageActionTranslations.Should().Be("PageActionTranslations");
        TableNames.Contents.Should().Be("Contents");
        TableNames.Medias.Should().Be("Medias");
        TableNames.Namespaces.Should().Be("Namespaces");
        TableNames.ResourceGroups.Should().Be("ResourceGroups");
        TableNames.EfMigrationHistory.Should().Be("_EFMigrationHistory");
        TableNames.MigrationHistory.Should().Be("_MigrationHistory");
    }

    [Fact]
    public void MigrationHistory_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var migrationId = "TestMigrationId";
        var migrationCompleted = true;

        // Act
        var migrationHistory = new MigrationHistory
        {
            MigrationId = migrationId,
            MigrationCompleted = migrationCompleted
        };

        // Assert
        migrationHistory.MigrationId.Should().Be(migrationId);
        migrationHistory.MigrationCompleted.Should().Be(migrationCompleted);
    }

    #region CMS

    [Fact]
    public void Content_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var key = "TestKey";
        var value = "TestValue";
        var languageId = 1;
        var namespaceSlug = "TestNamespaceSlug";
        var resourceGroupSlug = "TestResourceGroupSlug";
        var keyAlias = "TestNamespaceSlug.TestResourceGroupSlug.TestKey";
        var namespaceId = 1;
        var resourceGroupId = 1;
        var medias = new List<Media> { new() { Type = "image", Value = [1, 2, 3] } };

        // Act
        var content = new Content
        {
            Key = key,
            Value = value,
            LanguageId = languageId,
            NamespaceSlug = namespaceSlug,
            ResourceGroupSlug = resourceGroupSlug,
            KeyAlias = keyAlias,
            NamespaceId = namespaceId,
            ResourceGroupId = resourceGroupId,
            Medias = medias,
        };

        // Assert
        content.Key.Should().Be(key);
        content.Value.Should().Be(value);
        content.LanguageId.Should().Be(languageId);
        content.NamespaceSlug.Should().Be(namespaceSlug);
        content.ResourceGroupSlug.Should().Be(resourceGroupSlug);
        content.KeyAlias.Should().Be(keyAlias);
        content.NamespaceId.Should().Be(namespaceId);
        content.ResourceGroupId.Should().Be(resourceGroupId);
        content.Medias.Should().BeEquivalentTo(medias);
    }

    [Fact]
    public void Content_BuildKeyAlias_ShouldWorkCorrectly()
    {
        // Arrange
        var key = "TestKey";
        var value = "TestValue";
        var namespaceSlug = "TestNamespaceSlug";
        var resourceGroupSlug = "TestResourceGroupSlug";
        var expected = "TestNamespaceSlug.TestResourceGroupSlug.TestKey";

        // Act
        var content = new Content
        {
            Key = key,
            Value = value,
            NamespaceSlug = namespaceSlug,
            ResourceGroupSlug = resourceGroupSlug,
        };
        var result = content.BuildKeyAlias();

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void Media_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var value = new byte[] { 1, 2, 3 };
        var type = "image";
        var contentId = 1;
        var content = new Content { Key = "TestKey" };

        // Act
        var media = new Media
        {
            Value = value,
            Type = type,
            ContentId = contentId,
            Content = content
        };

        // Assert
        media.Value.Should().BeEquivalentTo(value);
        media.Type.Should().Be(type);
        media.ContentId.Should().Be(contentId);
        media.Content.Should().Be(content);
    }

    [Fact]
    public void Namespace_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var slug = "TestSlug";
        var name = "TestName";
        var description = "TestDescription";
        var resourceGroups = new List<ResourceGroup> { new() { Slug = "TestResourceGroupSlug", Name = "TestResourceGroupName" } };
        var contents = new List<Content> { new() { Key = "TestKey", Value = "TestValue" } };

        // Act
        var namespaceEntity = new Namespace
        {
            Slug = slug,
            Name = name,
            Description = description,
            ResourceGroups = resourceGroups,
            Contents = contents,
        };

        // Assert
        namespaceEntity.Slug.Should().Be(slug);
        namespaceEntity.Name.Should().Be(name);
        namespaceEntity.Description.Should().Be(description);
        namespaceEntity.ResourceGroups.Should().BeEquivalentTo(resourceGroups);
        namespaceEntity.Contents.Should().BeEquivalentTo(contents);
    }

    [Fact]
    public void ResourceGroup_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var slug = "TestSlug";
        var name = "TestName";
        var description = "TestDescription";
        var namespaceId = 1;
        var namespaceEntity = new Namespace { Id = namespaceId, Slug = "TestNamespaceSlug" };
        var contents = new List<Content> { new() { Key = "TestKey", Value = "TestValue" } };

        // Act
        var resourceGroup = new ResourceGroup
        {
            Slug = slug,
            Name = name,
            Description = description,
            NamespaceId = namespaceId,
            Namespace = namespaceEntity,
            Contents = contents,
        };

        // Assert
        resourceGroup.Slug.Should().Be(slug);
        resourceGroup.Name.Should().Be(name);
        resourceGroup.Description.Should().Be(description);
        resourceGroup.NamespaceId.Should().Be(namespaceId);
        resourceGroup.Namespace.Should().Be(namespaceEntity);
        resourceGroup.Contents.Should().BeEquivalentTo(contents);
    }

    #endregion

    #region UI

    [Fact]
    public void MenuGroup_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var translations = new List<MenuGroupTranslation>
        {
            new() { Name = "TestName", LanguageId = 1, EntityId = 1 }
        };
        var menuItems = new List<MenuItem>
        {
            new() { Url = "TestUrl", PageName = "TestPageName", PermissionOrGroupNames = ["Permission1"], GroupId = 1 }
        };

        // Act
        var menuGroup = new MenuGroup
        {
            Translations = translations,
            MenuItems = menuItems
        };

        // Assert
        menuGroup.Translations.Should().BeEquivalentTo(translations);
        menuGroup.MenuItems.Should().BeEquivalentTo(menuItems);
    }

    [Fact]
    public void MenuItem_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var url = "http://example.com";
        var pageName = "HomePage";
        var permissionOrGroupNames = new List<string> { "UserManagement", "UserManagement.List" };
        var translations = new List<MenuItemTranslation> { new() { Name = "Home", LanguageId = 1, EntityId = 1 } };
        var groupId = 1;
        var group = new MenuGroup();
        var parentId = 2;
        var parent = new MenuItem();
        var childrens = new List<MenuItem> { new() { Url = "http://example.com/child" } };

        // Act
        var menuItem = new MenuItem
        {
            Url = url,
            PageName = pageName,
            PermissionOrGroupNames = permissionOrGroupNames,
            Translations = translations,
            GroupId = groupId,
            Group = group,
            ParentId = parentId,
            Parent = parent,
            Childrens = childrens,
        };

        // Assert
        menuItem.Url.Should().Be(url);
        menuItem.PageName.Should().Be(pageName);
        menuItem.PermissionOrGroupNames.Should().BeEquivalentTo(permissionOrGroupNames);
        menuItem.Translations.Should().BeEquivalentTo(translations);
        menuItem.GroupId.Should().Be(groupId);
        menuItem.Group.Should().Be(group);
        menuItem.ParentId.Should().Be(parentId);
        menuItem.Parent.Should().Be(parent);
        menuItem.Childrens.Should().BeEquivalentTo(childrens);
    }

    [Fact]
    public void Page_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var name = "TestPage";
        var hasCreate = true;
        var hasDetail = true;
        var hasEdit = true;
        var hasDelete = true;
        var createPermissions = new List<string> { "CreatePermission1", "CreatePermission2" };
        var detailPermissions = new List<string> { "DetailPermission1", "DetailPermission2" };
        var editPermissions = new List<string> { "EditPermission1", "EditPermission2" };
        var deletePermissions = new List<string> { "DeletePermission1", "DeletePermission2" };
        var additionalActions = new List<PageAction>
        {
            new() {
                ActionName = "Action1",
                Permissions = ["Permission1"],
                Translations =
                [
                    new() { Title = "Title1", LanguageId = 1, EntityId = 1 }
                ],
                PageId = 1
            }
        };

        // Act
        var page = new Page
        {
            Name = name,
            HasCreate = hasCreate,
            HasDetail = hasDetail,
            HasEdit = hasEdit,
            HasDelete = hasDelete,
            CreatePermissions = createPermissions,
            DetailPermissions = detailPermissions,
            EditPermissions = editPermissions,
            DeletePermissions = deletePermissions,
            AdditionalActions = additionalActions
        };

        // Assert
        page.Name.Should().Be(name);
        page.HasCreate.Should().Be(hasCreate);
        page.HasDetail.Should().Be(hasDetail);
        page.HasEdit.Should().Be(hasEdit);
        page.HasDelete.Should().Be(hasDelete);
        page.CreatePermissions.Should().BeEquivalentTo(createPermissions);
        page.DetailPermissions.Should().BeEquivalentTo(detailPermissions);
        page.EditPermissions.Should().BeEquivalentTo(editPermissions);
        page.DeletePermissions.Should().BeEquivalentTo(deletePermissions);
        page.AdditionalActions.Should().BeEquivalentTo(additionalActions);
    }

    [Fact]
    public void PageAction_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var actionName = "TestAction";
        var permissions = new List<string> { "Permission1", "Permission2" };
        var translations = new List<PageActionTranslation>
        {
            new() { Title = "Title1", LanguageId = 1, EntityId = 1 },
            new() { Title = "Title2", LanguageId = 2, EntityId = 2 }
        };
        var pageId = 1;
        var page = new Page { Name = "TestPage", HasCreate = true, HasDetail = true, HasEdit = true, HasDelete = true };

        // Act
        var pageAction = new PageAction
        {
            ActionName = actionName,
            Permissions = permissions,
            Translations = translations,
            PageId = pageId,
            Page = page
        };

        // Assert
        pageAction.ActionName.Should().Be(actionName);
        pageAction.Permissions.Should().BeEquivalentTo(permissions);
        pageAction.Translations.Should().BeEquivalentTo(translations);
        pageAction.PageId.Should().Be(pageId);
        pageAction.Page.Should().Be(page);
    }

    #endregion

    [Fact]
    public void ActivityLog_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var userName = "TestUser";
        var activity = UserActivity.CreateUser;
        var activityDate = DateTimeOffset.Now;

        // Act
        var activityLog = new ActivityLog
        {
            UserName = userName,
            Activity = activity,
            ActivityDate = activityDate
        };

        // Assert
        activityLog.UserName.Should().Be(userName);
        activityLog.Activity.Should().Be(activity);
        activityLog.ActivityDate.Should().Be(activityDate);
    }

    [Fact]
    public void Language_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var id = 1;
        var name = "English";
        var code = "en";
        var supported = true;
        var isDefault = true;

        // Act
        var language = new Language
        {
            Id = id,
            Name = name,
            Code = code,
            Supported = supported,
            IsDefault = isDefault
        };

        // Assert
        language.Id.Should().Be(id);
        language.Name.Should().Be(name);
        language.Code.Should().Be(code);
        language.IsDefault.Should().Be(isDefault);
        language.Supported.Should().Be(supported);
    }

    [Fact]
    public void GetUniqueIdentifier_ShouldReturnId()
    {
        // Arrange
        var language = new Language { Id = 1 };

        // Act
        var result = language.GetUniqueIdentifier();

        // Assert
        result.Should().Be(1);
    }

    [Fact]
    public void ToString_ShouldReturnFormattedString()
    {
        // Arrange
        var language = new Language { Id = 1 };

        // Act
        var result = language.ToString();

        // Assert
        result.Should().Be("[Language 1]");
    }

    [Fact]
    public void Permission_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var name = "TestName";
        var description = "TestDescription";
        var normalizedName = "TESTNAME";
        var permissionGroup = "TestGroup";
        var permissionGroupDescription = "TestGroupDescription";
        var rolePermissionRelations = new List<RolePermissionRelation> { new() { RoleId = 1, PermissionId = 1 } };

        // Act
        var permission = new Permission
        {
            Name = name,
            Description = description,
            NormalizedName = normalizedName,
            PermissionGroup = permissionGroup,
            PermissionGroupDescription = permissionGroupDescription,
            RolePermissionRelations = rolePermissionRelations,
        };

        // Assert
        permission.Name.Should().Be(name);
        permission.Description.Should().Be(description);
        permission.NormalizedName.Should().Be(normalizedName);
        permission.PermissionGroup.Should().Be(permissionGroup);
        permission.PermissionGroupDescription.Should().Be(permissionGroupDescription);
        permission.RolePermissionRelations.Should().BeEquivalentTo(rolePermissionRelations);
    }

    [Fact]
    public void Permission_FormatPermissionAndGroup_ShouldWorkCorrectly()
    {
        // Arrange
        var name = "TestName";
        var description = "TestDescription";
        var normalizedName = "TESTNAME";
        var permissionGroup = "TestGroup";
        var permissionGroupDescription = "TestGroupDescription";
        var rolePermissionRelations = new List<RolePermissionRelation> { new() { RoleId = 1, PermissionId = 1 } };

        // Act
        var permission = new Permission
        {
            Name = name,
            Description = description,
            NormalizedName = normalizedName,
            PermissionGroup = permissionGroup,
            PermissionGroupDescription = permissionGroupDescription,
            RolePermissionRelations = rolePermissionRelations,
        };
        var result = permission.FormatPermissionAndGroup();

        // Assert
        result.Should().Be("TestGroup.TestName");
    }

    [Fact]
    public void Role_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var name = "Admin";
        var userRoleRelations = new List<UserRoleRelation> { new() { UserId = 1, RoleId = 1 } };
        var rolePermissionRelations = new List<RolePermissionRelation> { new() { RoleId = 1, PermissionId = 1 } };

        // Act
        var role = new Role
        {
            Name = name,
            UserRoleRelations = userRoleRelations,
            RolePermissionRelations = rolePermissionRelations
        };

        // Assert
        role.Name.Should().Be(name);
        role.UserRoleRelations.Should().BeEquivalentTo(userRoleRelations);
        role.RolePermissionRelations.Should().BeEquivalentTo(rolePermissionRelations);
    }

    [Fact]
    public void RolePermissionRelation_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var roleId = 1;
        var permissionId = 2;
        var role = new Role { Name = "Admin" };
        var permission = new Permission { Name = "Read" };

        // Act
        var rolePermissionRelation = new RolePermissionRelation
        {
            RoleId = roleId,
            PermissionId = permissionId,
            Role = role,
            Permission = permission
        };

        // Assert
        rolePermissionRelation.RoleId.Should().Be(roleId);
        rolePermissionRelation.PermissionId.Should().Be(permissionId);
        rolePermissionRelation.Role.Should().Be(role);
        rolePermissionRelation.Permission.Should().Be(permission);
    }

    [Fact]
    public void User_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var id = 1;
        var userName = "TestUserName";
        var accessFailedCount = 0;
        var email = "test@example.com";
        var creationDate = DateTime.Now;
        var creatorUserName = "CreatorUser";
        var lastModificationDate = DateTime.Now;
        var lastModifierUserName = "ModifierUser";
        var deletionDate = DateTime.Now;
        var deleterUserName = "DeleterUser";
        var name = "TestName";
        var surname = "TestSurname";
        var userType = UserType.AppUser;
        var emailConfirmed = true;
        var phoneNumber = "1234567890";
        var phoneNumberConfirmed = true;
        var sessions = new List<UserSession> { new() { AccessToken = "AccessToken", RefreshToken = "RefreshToken" } };
        var lockoutEnabled = true;
        var lockoutEnd = DateTime.Now;
        var normalizedEmail = "TEST@EXAMPLE.COM";
        var normalizedUserName = "TESTUSERNAME";
        var passwordHash = "PasswordHash";
        var twoFactorEnabled = true;
        var roleRelations = new List<UserRoleRelation> { new() { RoleId = 1, UserId = 1 } };
        var isDeleted = false;

        // Act
        var user = new User
        {
            Id = id,
            UserName = userName,
            AccessFailedCount = accessFailedCount,
            Email = email,
            CreationDate = creationDate,
            CreatorUserName = creatorUserName,
            LastModificationDate = lastModificationDate,
            LastModifierUserName = lastModifierUserName,
            DeletionDate = deletionDate,
            DeleterUserName = deleterUserName,
            Name = name,
            Surname = surname,
            UserType = userType,
            EmailConfirmed = emailConfirmed,
            PhoneNumber = phoneNumber,
            PhoneNumberConfirmed = phoneNumberConfirmed,
            Sessions = sessions,
            LockoutEnabled = lockoutEnabled,
            LockoutEnd = lockoutEnd,
            NormalizedEmail = normalizedEmail,
            NormalizedUserName = normalizedUserName,
            PasswordHash = passwordHash,
            TwoFactorEnabled = twoFactorEnabled,
            RoleRelations = roleRelations,
            IsDeleted = isDeleted,
        };

        // Assert
        user.Id.Should().Be(id);
        user.UserName.Should().Be(userName);
        user.AccessFailedCount.Should().Be(accessFailedCount);
        user.Email.Should().Be(email);
        user.CreationDate.Should().Be(creationDate);
        user.CreatorUserName.Should().Be(creatorUserName);
        user.LastModificationDate.Should().Be(lastModificationDate);
        user.LastModifierUserName.Should().Be(lastModifierUserName);
        user.DeletionDate.Should().Be(deletionDate);
        user.DeleterUserName.Should().Be(deleterUserName);
        user.Name.Should().Be(name);
        user.Surname.Should().Be(surname);
        user.UserType.Should().Be(userType);
        user.EmailConfirmed.Should().Be(emailConfirmed);
        user.PhoneNumber.Should().Be(phoneNumber);
        user.PhoneNumberConfirmed.Should().Be(phoneNumberConfirmed);
        user.Sessions.Should().BeEquivalentTo(sessions);
        user.LockoutEnabled.Should().Be(lockoutEnabled);
        user.LockoutEnd.Should().Be(lockoutEnd);
        user.NormalizedEmail.Should().Be(normalizedEmail);
        user.NormalizedUserName.Should().Be(normalizedUserName);
        user.PasswordHash.Should().Be(passwordHash);
        user.TwoFactorEnabled.Should().Be(twoFactorEnabled);
        user.RoleRelations.Should().BeEquivalentTo(roleRelations);
        user.IsDeleted.Should().Be(isDeleted);
    }

    [Fact]
    public void User_Projections_UserRemove_ShouldReturnCorrectly()
    {
        // Arrange
        Expression<Func<User, User>> expectedExpression = u => new User
        {
            Id = u.Id,
            UserName = u.UserName,
            AccessFailedCount = u.AccessFailedCount,
            Email = u.Email,
            CreationDate = u.CreationDate,
            CreatorUserName = u.CreatorUserName,
            LastModificationDate = u.LastModificationDate,
            LastModifierUserName = u.LastModifierUserName,
            DeletionDate = u.DeletionDate,
            DeleterUserName = u.DeleterUserName,
            Name = u.Name,
            Surname = u.Surname,
            UserType = u.UserType,
            EmailConfirmed = u.EmailConfirmed,
            PhoneNumber = u.PhoneNumber,
            PhoneNumberConfirmed = u.PhoneNumberConfirmed,
            Sessions = u.Sessions,
            LockoutEnabled = u.LockoutEnabled,
            LockoutEnd = u.LockoutEnd,
            NormalizedEmail = u.NormalizedEmail,
            NormalizedUserName = u.NormalizedUserName,
            PasswordHash = u.PasswordHash,
            TwoFactorEnabled = u.TwoFactorEnabled,
            RoleRelations = u.RoleRelations,
            IsDeleted = u.IsDeleted,
        };

        // Act
        var result = User.Projections.UserRemove;

        // Assert
        var equality = ExpressionEqualityComparer.Instance.Equals(expectedExpression, result);

        equality.Should().BeTrue();
    }

    [Fact]
    public void User_Projections_GenerateToken_ShouldReturnCorrectly()
    {
        // Arrange
        Expression<Func<User, User>> expectedExpression = u => new User
        {
            Id = u.Id,
            UserName = u.UserName,
            UserType = u.UserType,
            RoleRelations = u.RoleRelations.Select(r => new UserRoleRelation
            {
                Id = r.Id,
                Role = new Role
                {
                    Id = r.Role.Id,
                    Name = r.Role.Name,
                    RolePermissionRelations = r.Role.RolePermissionRelations.Select(rp => new RolePermissionRelation
                    {
                        Id = rp.Id,
                        PermissionId = rp.PermissionId,
                        RoleId = rp.RoleId,
                        Permission = new Permission
                        {
                            Id = rp.Permission.Id,
                            Name = rp.Permission.Name,
                            PermissionGroup = rp.Permission.PermissionGroup
                        }
                    }).ToList()
                },
                UserId = r.UserId,
                RoleId = r.RoleId

            }).ToList(),
            Sessions = u.Sessions.Select(s => new UserSession
            {
                Id = s.Id,
                AccessToken = s.AccessToken,
                RefreshToken = s.RefreshToken,
                UserId = s.UserId,
                DeviceId = s.DeviceId,
                CreationDate = s.CreationDate,
                IpAddress = s.IpAddress,
                ExpiryDate = s.ExpiryDate,
            }).ToList(),
            IsDeleted = u.IsDeleted,
        };

        // Act
        var result = User.Projections.GenerateToken;

        // Assert
        var equality = ExpressionEqualityComparer.Instance.Equals(expectedExpression, result);

        equality.Should().BeTrue();
    }

    [Fact]
    public void User_Projections_Login_ShouldReturnCorrectly()
    {
        // Arrange
        Expression<Func<User, User>> expectedExpression = u => new User
        {
            Id = u.Id,
            UserName = u.UserName,
            PasswordHash = u.PasswordHash,
            UserType = u.UserType,
            AccessFailedCount = u.AccessFailedCount,
            LockoutEnabled = u.LockoutEnabled,
            LockoutEnd = u.LockoutEnd,
            Sessions = u.Sessions.Select(s => new UserSession
            {
                Id = s.Id,
                AccessToken = s.AccessToken,
                RefreshToken = s.RefreshToken,
                UserId = s.UserId,
                DeviceId = s.DeviceId,
                CreationDate = s.CreationDate,
                IpAddress = s.IpAddress,
                ExpiryDate = s.ExpiryDate,
            }).ToList(),
            RoleRelations = u.RoleRelations.Select(r => new UserRoleRelation
            {
                Id = r.Id,
                Role = new Role
                {
                    Id = r.Role.Id,
                    Name = r.Role.Name,
                    RolePermissionRelations = r.Role.RolePermissionRelations.Select(rp => new RolePermissionRelation
                    {
                        Id = rp.Id,
                        PermissionId = rp.PermissionId,
                        RoleId = rp.RoleId,
                        Permission = new Permission
                        {
                            Id = rp.Permission.Id,
                            Name = rp.Permission.Name,
                            PermissionGroup = rp.Permission.PermissionGroup
                        }
                    }).ToList()
                },
                UserId = r.UserId,
                RoleId = r.RoleId

            }).ToList(),
            IsDeleted = u.IsDeleted,
        };

        // Act
        var result = User.Projections.Login;

        // Assert
        var equality = ExpressionEqualityComparer.Instance.Equals(expectedExpression, result);

        equality.Should().BeTrue();
    }

    [Fact]
    public void User_Projections_Permissions_ShouldReturnCorrectly()
    {
        // Arrange
        Expression<Func<User, User>> expectedExpression = u => new User
        {
            Id = u.Id,
            UserName = u.UserName,
            RoleRelations = u.RoleRelations.Select(r => new UserRoleRelation
            {
                Id = r.Id,
                Role = new Role
                {
                    Id = r.Role.Id,
                    Name = r.Role.Name,
                    RolePermissionRelations = r.Role.RolePermissionRelations.Select(rp => new RolePermissionRelation
                    {
                        Id = rp.Id,
                        PermissionId = rp.PermissionId,
                        RoleId = rp.RoleId,
                        Permission = new Permission
                        {
                            Id = rp.Permission.Id,
                            Name = rp.Permission.Name,
                            PermissionGroup = rp.Permission.PermissionGroup
                        }
                    }).ToList()
                },
                UserId = r.UserId,
                RoleId = r.RoleId

            }).ToList(),
        };

        // Act
        var result = User.Projections.Permissions;

        // Assert
        var equality = ExpressionEqualityComparer.Instance.Equals(expectedExpression, result);

        equality.Should().BeTrue();
    }

    [Fact]
    public void User_Projections_ChangePassword_ShouldReturnCorrectly()
    {
        // Arrange
        Expression<Func<User, User>> expectedExpression = u => new User
        {
            Id = u.Id,
            UserName = u.UserName,
            Email = u.Email,
            PasswordHash = u.PasswordHash,
            Sessions = u.Sessions.Select(s => new UserSession
            {
                Id = s.Id,
                AccessToken = s.AccessToken,
                RefreshToken = s.RefreshToken,
                UserId = s.UserId,
                DeviceId = s.DeviceId,
                CreationDate = s.CreationDate,
                ExpiryDate = s.ExpiryDate,
                UserName = s.UserName,
                IpAddress = s.IpAddress
            }).ToList(),
            IsDeleted = u.IsDeleted,
        };

        // Act
        var result = User.Projections.ChangePassword;

        // Assert
        var equality = ExpressionEqualityComparer.Instance.Equals(expectedExpression, result);

        equality.Should().BeTrue();
    }

    [Fact]
    public void UserRoleRelation_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var userId = 1;
        var roleId = 2;
        var role = new Role { Name = "Admin" };
        var user = new User { Name = "John", Surname = "Doe" };

        // Act
        var userRoleRelation = new UserRoleRelation
        {
            UserId = userId,
            RoleId = roleId,
            Role = role,
            User = user
        };

        // Assert
        userRoleRelation.UserId.Should().Be(userId);
        userRoleRelation.RoleId.Should().Be(roleId);
        userRoleRelation.Role.Should().Be(role);
        userRoleRelation.User.Should().Be(user);
    }

    [Fact]
    public void UserSession_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var userName = "TestUser";
        var accessToken = "TestAccessToken";
        var refreshToken = "TestRefreshToken";
        var expiryDate = DateTime.Now.AddHours(1);
        var deviceId = "TestDeviceId";
        var userId = 1;
        var user = new User { Name = "Test", Surname = "User" };

        // Act
        var userSession = new UserSession
        {
            UserName = userName,
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiryDate = expiryDate,
            DeviceId = deviceId,
            UserId = userId,
            User = user,
        };

        // Assert
        userSession.UserName.Should().Be(userName);
        userSession.AccessToken.Should().Be(accessToken);
        userSession.RefreshToken.Should().Be(refreshToken);
        userSession.ExpiryDate.Should().Be(expiryDate);
        userSession.DeviceId.Should().Be(deviceId);
        userSession.UserId.Should().Be(userId);
        userSession.User.Should().Be(user);
    }

    [Fact]
    public void UserSession_Conditions_CurrentSession_ShouldReturnCorrectly()
    {
        var userName = "milvasoft";
        var deviceId = "milva";
        var testObject = new UserSession { UserName = "milvasoft", DeviceId = "milva" };

        Expression<Func<UserSession, bool>> expectedExpression = s => s.UserName == userName && s.DeviceId == deviceId;

        // Act
        var result = UserSession.Conditions.CurrentSession(userName, deviceId);
        var compiledExpected = expectedExpression.Compile();
        var compiledResult = result.Compile();

        // Assert
        compiledExpected(testObject).Should().Be(compiledResult(testObject));
    }

    [Fact]
    public void UserSession_Conditions_DeleteAllSessions_ShouldReturnCorrectly()
    {
        var userName = "milvasoft";
        var testObject = new UserSession { UserName = "milvasoft", DeviceId = "milva" };

        Expression<Func<UserSession, bool>> expectedExpression = s => s.UserName == userName;

        // Act
        var result = UserSession.Conditions.DeleteAllSessions(userName);
        var compiledExpected = expectedExpression.Compile();
        var compiledResult = result.Compile();

        // Assert
        compiledExpected(testObject).Should().Be(compiledResult(testObject));
    }

    [Fact]
    public void UserSession_Projections_CurrentSession_ShouldReturnCorrectly()
    {
        // Arrange
        Expression<Func<UserSession, UserSession>> expectedExpression = s => new UserSession
        {
            Id = s.Id,
            AccessToken = s.AccessToken,
            RefreshToken = s.RefreshToken,
            UserName = s.UserName,
            UserId = s.UserId,
            DeviceId = s.DeviceId,
            CreationDate = s.CreationDate,
            ExpiryDate = s.ExpiryDate,
            IpAddress = s.IpAddress,
        };

        // Act
        var result = UserSession.Projections.CurrentSession;

        // Assert
        var equality = ExpressionEqualityComparer.Instance.Equals(expectedExpression, result);

        equality.Should().BeTrue();
    }
}