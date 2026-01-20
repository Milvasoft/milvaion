using FluentAssertions;
using Milvaion.Application.Utils.PermissionManager;
using Milvasoft.Core.Helpers;

namespace Milvaion.UnitTests.UtilsTests;

[Trait("Utils Unit Tests", "PermissionCatalog unit tests.")]
public class PermissionCatalogTests
{
    [Fact]
    public void GetPermissionGroups_ShouldReturnCorrectPermissions()
    {
        // Act
        var result = PermissionCatalog.GetPermissionsAndGroups();

        // Assert
        result.Should().NotBeNull();
        result.Should().ContainKey("App");
        result.Should().ContainKey("UserManagement");
        result.Should().ContainKey("RoleManagement");
        result.Should().ContainKey("ContentManagement");
        result.Should().ContainKey("NamespaceManagement");
        result.Should().ContainKey("ResourceGroupManagement");
        result.Should().ContainKey("PermissionManagement");
        result.Should().ContainKey("ActivityLogManagement");
        result.Should().ContainKey("LanguageManagement");

        result["App"].Should().Contain(p => p.Name == "SuperAdmin");
        result["UserManagement"].Should().Contain(p => p.Name == "List");
        result["UserManagement"].Should().Contain(p => p.Name == "Detail");
        result["UserManagement"].Should().Contain(p => p.Name == "Create");
        result["UserManagement"].Should().Contain(p => p.Name == "Update");
        result["UserManagement"].Should().Contain(p => p.Name == "Delete");
        result["RoleManagement"].Should().Contain(p => p.Name == "List");
        result["RoleManagement"].Should().Contain(p => p.Name == "Detail");
        result["RoleManagement"].Should().Contain(p => p.Name == "Create");
        result["RoleManagement"].Should().Contain(p => p.Name == "Update");
        result["RoleManagement"].Should().Contain(p => p.Name == "Delete");
        result["ContentManagement"].Should().Contain(p => p.Name == "List");
        result["ContentManagement"].Should().Contain(p => p.Name == "Detail");
        result["ContentManagement"].Should().Contain(p => p.Name == "Create");
        result["ContentManagement"].Should().Contain(p => p.Name == "Update");
        result["ContentManagement"].Should().Contain(p => p.Name == "Delete");
        result["NamespaceManagement"].Should().Contain(p => p.Name == "List");
        result["NamespaceManagement"].Should().Contain(p => p.Name == "Detail");
        result["NamespaceManagement"].Should().Contain(p => p.Name == "Create");
        result["NamespaceManagement"].Should().Contain(p => p.Name == "Update");
        result["NamespaceManagement"].Should().Contain(p => p.Name == "Delete");
        result["ResourceGroupManagement"].Should().Contain(p => p.Name == "List");
        result["ResourceGroupManagement"].Should().Contain(p => p.Name == "Detail");
        result["ResourceGroupManagement"].Should().Contain(p => p.Name == "Create");
        result["ResourceGroupManagement"].Should().Contain(p => p.Name == "Update");
        result["ResourceGroupManagement"].Should().Contain(p => p.Name == "Delete");
        result["PermissionManagement"].Should().Contain(p => p.Name == "List");
        result["ActivityLogManagement"].Should().Contain(p => p.Name == "List");
        result["LanguageManagement"].Should().Contain(p => p.Name == "List");
        result["LanguageManagement"].Should().Contain(p => p.Name == "Update");
    }

    [Fact]
    public void GetPermissions_ShouldReturnCorrectPermissions()
    {
        // Arrange
        var type = typeof(PermissionCatalog.UserManagement);

        // Act
        var permissions = PermissionBase.GetPermissions(type).ToList();

        // Assert
        permissions.Should().HaveCount(5);

        permissions[0].Name.Should().Be("List");
        permissions[0].NormalizedName.Should().Be("List".MilvaNormalize());
        permissions[0].Description.Should().Be("User list permission.");
        permissions[0].PermissionGroup.Should().Be("UserManagement");
        permissions[0].PermissionGroupDescription.Should().Be("User Management");

        permissions[1].Name.Should().Be("Detail");
        permissions[1].NormalizedName.Should().Be("Detail".MilvaNormalize());
        permissions[1].Description.Should().Be("User detail view permission");
        permissions[1].PermissionGroup.Should().Be("UserManagement");
        permissions[1].PermissionGroupDescription.Should().Be("User Management");

        permissions[2].Name.Should().Be("Create");
        permissions[2].NormalizedName.Should().Be("Create".MilvaNormalize());
        permissions[2].Description.Should().Be("User create permission");
        permissions[2].PermissionGroup.Should().Be("UserManagement");
        permissions[2].PermissionGroupDescription.Should().Be("User Management");

        permissions[3].Name.Should().Be("Update");
        permissions[3].NormalizedName.Should().Be("Update".MilvaNormalize());
        permissions[3].Description.Should().Be("User update permission");
        permissions[3].PermissionGroup.Should().Be("UserManagement");
        permissions[3].PermissionGroupDescription.Should().Be("User Management");

        permissions[4].Name.Should().Be("Delete");
        permissions[4].NormalizedName.Should().Be("Delete".MilvaNormalize());
        permissions[4].Description.Should().Be("User delete permission");
        permissions[4].PermissionGroup.Should().Be("UserManagement");
        permissions[4].PermissionGroupDescription.Should().Be("User Management");
    }
}
