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

    [Fact]
    public void OnAuthorization_UserTypeMatches_ShouldNotSetForbiddenStatusCode()
    {
        // Arrange
        var userType = UserType.Manager;
        var attribute = new UserTypeAuthAttribute(userType);
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        var claims = new List<Claim> {
            new(GlobalConstant.UserTypeClaimName, UserType.Manager.ToString())
        };
        var identity = new ClaimsIdentity(claims, "TestAuthentication");
        httpContext.User = new ClaimsPrincipal(identity);
        var contextMock = new Mock<AuthorizationFilterContext>(new ActionContext
        {
            HttpContext = httpContext,
            RouteData = new RouteData(),
            ActionDescriptor = new ActionDescriptor()
        }, new List<IFilterMetadata>());
        var serviceProviderMock = new Mock<IServiceProvider>();
        var tokenManagerMock = new Mock<IMilvaTokenManager>();

        tokenManagerMock.Setup(t => t.GetClaimsPrincipalIfValid(It.IsAny<string>())).Returns(new ClaimsPrincipal(new ClaimsIdentity(claims)));
        serviceProviderMock.Setup(sp => sp.GetService(typeof(IMilvaTokenManager))).Returns(tokenManagerMock.Object);
        httpContext.RequestServices = serviceProviderMock.Object;
        httpContext.Request.Headers.Authorization = "Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJodHRwOi8vc2NoZW1hcy5taWNyb3NvZnQuY29tL3dzLzIwMDgvMDYvaWRlbnRpdHkvY2xhaW1zL2V4cGlyZWQiOiIyMi4xMi4yMDI0IDExOjM0OjE5IiwidW5pcXVlX25hbWUiOiJidWdyYWtvc2VuIiwicm9sZSI6IkFwcC5TdXBlckFkbWluIiwidXQiOiJNYW5hZ2VyIiwibmJmIjoxNzM0ODY1NDU5LCJleHAiOjE3MzQ4NjcyNTksImlhdCI6MTczNDg2NTQ1OX0.Qc2xXhAQYNiT6kAKTVgmQOJbpGd-YKLeKCbRvX-cP6s";

        // Act
        attribute.OnAuthorization(contextMock.Object);

        // Assert
        httpContext.Response.StatusCode.Should().Be((int)HttpStatusCode.OK);
    }

    [Fact]
    public void OnAuthorization_UserTypeDoesNotMatch_ShouldSetForbiddenStatusCode()
    {
        // Arrange
        var userType = UserType.AppUser;
        var attribute = new UserTypeAuthAttribute(userType);
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        var claims = new List<Claim> {
            new(GlobalConstant.UserTypeClaimName, UserType.Manager.ToString())
        };
        var identity = new ClaimsIdentity(claims, "TestAuthentication");
        httpContext.User = new ClaimsPrincipal(identity);
        var contextMock = new Mock<AuthorizationFilterContext>(new ActionContext
        {
            HttpContext = httpContext,
            RouteData = new RouteData(),
            ActionDescriptor = new ActionDescriptor()
        }, new List<IFilterMetadata>());
        var serviceProviderMock = new Mock<IServiceProvider>();
        var tokenManagerMock = new Mock<IMilvaTokenManager>();

        tokenManagerMock.Setup(t => t.GetClaimsPrincipalIfValid(It.IsAny<string>())).Returns(new ClaimsPrincipal(new ClaimsIdentity(claims)));
        serviceProviderMock.Setup(sp => sp.GetService(typeof(IMilvaTokenManager))).Returns(tokenManagerMock.Object);
        httpContext.RequestServices = serviceProviderMock.Object;
        httpContext.Request.Headers.Authorization = "Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJodHRwOi8vc2NoZW1hcy5taWNyb3NvZnQuY29tL3dzLzIwMDgvMDYvaWRlbnRpdHkvY2xhaW1zL2V4cGlyZWQiOiIyMi4xMi4yMDI0IDExOjM0OjE5IiwidW5pcXVlX25hbWUiOiJidWdyYWtvc2VuIiwicm9sZSI6IkFwcC5TdXBlckFkbWluIiwidXQiOiJNYW5hZ2VyIiwibmJmIjoxNzM0ODY1NDU5LCJleHAiOjE3MzQ4NjcyNTksImlhdCI6MTczNDg2NTQ1OX0.Qc2xXhAQYNiT6kAKTVgmQOJbpGd-YKLeKCbRvX-cP6s";

        // Act
        Action act = () => attribute.OnAuthorization(contextMock.Object);

        // Assert
        act.Should().Throw<MilvaUserFriendlyException>();
        httpContext.Response.StatusCode.Should().Be((int)HttpStatusCode.Forbidden);
    }

    [Fact]
    public void OnAuthorization_UserTypeIsNull_ShouldSetForbiddenStatusCode()
    {
        // Arrange
        var userType = UserType.Manager;
        var attribute = new UserTypeAuthAttribute(userType);
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        var claims = new List<Claim>
        {
        };
        var identity = new ClaimsIdentity(claims, "TestAuthentication");
        httpContext.User = new ClaimsPrincipal(identity);
        var contextMock = new Mock<AuthorizationFilterContext>(new ActionContext
        {
            HttpContext = httpContext,
            RouteData = new RouteData(),
            ActionDescriptor = new ActionDescriptor()
        }, new List<IFilterMetadata>());
        var serviceProviderMock = new Mock<IServiceProvider>();
        var tokenManagerMock = new Mock<IMilvaTokenManager>();

        tokenManagerMock.Setup(t => t.GetClaimsPrincipalIfValid(It.IsAny<string>())).Returns(new ClaimsPrincipal(new ClaimsIdentity(claims)));
        serviceProviderMock.Setup(sp => sp.GetService(typeof(IMilvaTokenManager))).Returns(tokenManagerMock.Object);
        httpContext.RequestServices = serviceProviderMock.Object;
        httpContext.Request.Headers.Authorization = "Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJodHRwOi8vc2NoZW1hcy5taWNyb3NvZnQuY29tL3dzLzIwMDgvMDYvaWRlbnRpdHkvY2xhaW1zL2V4cGlyZWQiOiIyMi4xMi4yMDI0IDExOjM0OjE5IiwidW5pcXVlX25hbWUiOiJidWdyYWtvc2VuIiwicm9sZSI6IkFwcC5TdXBlckFkbWluIiwidXQiOiJNYW5hZ2VyIiwibmJmIjoxNzM0ODY1NDU5LCJleHAiOjE3MzQ4NjcyNTksImlhdCI6MTczNDg2NTQ1OX0.Qc2xXhAQYNiT6kAKTVgmQOJbpGd-YKLeKCbRvX-cP6s";

        // Act
        Action act = () => attribute.OnAuthorization(contextMock.Object);

        // Assert
        act.Should().Throw<MilvaUserFriendlyException>();
        httpContext.Response.StatusCode.Should().Be((int)HttpStatusCode.Forbidden);
    }
}
