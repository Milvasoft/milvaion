using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Milvaion.Application.Utils.Attributes;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.PermissionManager;
using Milvaion.Domain.Enums;
using Milvasoft.Core.Exceptions;
using Milvasoft.Identity.Abstract;
using Moq;
using System.Net;
using System.Security.Claims;

namespace Milvaion.UnitTests.UtilsTests;

[Trait("Utils Unit Tests", "Attributes unit tests.")]
public class AttributesTests
{
    [Fact]
    public void AuthAttribute_DefaultConstructor_ShouldSetSuperAdminRole()
    {
        // Act
        var attribute = new AuthAttribute();

        // Assert
        attribute.Roles.Should().BeNull();
    }

    [Fact]
    public void AuthAttribute_ConstructorWithRoles_ShouldSetRolesCorrectly()
    {
        // Arrange
        var roles = new[] { "Role1", "Role2" };
        var expectedRoles = $"{PermissionCatalog.App.SuperAdmin},Role1,Role2";

        // Act
        var attribute = new AuthAttribute(roles);

        // Assert
        attribute.Roles.Should().Be(expectedRoles);
    }

    [Fact]
    public void AuthAttribute_ConstructorWithEmptyRoles_ShouldSetSuperAdminRole()
    {
        // Arrange
        var roles = Array.Empty<string>();
        var expectedRoles = PermissionCatalog.App.SuperAdmin + ",";

        // Act
        var attribute = new AuthAttribute(roles);

        // Assert
        attribute.Roles.Should().Be(expectedRoles);
    }

    [Fact]
    public void Activity_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var activity = UserActivity.CreateUser;

        // Act
        var attribute = new UserActivityTrackAttribute(activity);

        // Assert
        attribute.Activity.Should().Be(activity);
    }
}
