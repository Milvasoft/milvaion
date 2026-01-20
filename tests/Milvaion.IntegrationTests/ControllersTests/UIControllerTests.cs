using FluentAssertions;
using Milvaion.Application.Dtos.UIDtos;
using Milvaion.Application.Dtos.UIDtos.MenuItemDtos;
using Milvaion.Application.Dtos.UIDtos.PageDtos;
using Milvaion.Application.Utils.Constants;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Components.Rest.MilvaResponse;
using System.Net;
using System.Net.Http.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.ControllersTests;

[Collection(nameof(MilvaionTestCollection))]
[Trait("Controller Integration Tests", "Integration tests for UIController.")]
public class UIControllerTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    private const string _baseUrl = $"{GlobalConstant.RoutePrefix}/v1.0/ui";

    #region GetAccessibleMenuItems

    [Fact]
    public async Task GetAccessibleMenuItemsAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Act
        var httpResponse = await _factory.CreateClient().GetAsync($"{_baseUrl}/menuItems");

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetAccessibleMenuItemsAsync_WithAuthorization_ShouldReturnMenuItems()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/menuItems");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<List<MenuItemDto>>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.StatusCode.Should().Be((int)HttpStatusCode.OK);
    }

    #endregion

    #region GetPageAccessibilityForCurrentUser

    [Fact]
    public async Task GetPageAccessibilityAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Act
        var httpResponse = await _factory.CreateClient().GetAsync($"{_baseUrl}/pages/page?PageName=TestPage");

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetPageAccessibilityAsync_WithAuthorization_ShouldReturnPageInfo()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/pages/page?PageName=TestPage");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<PageDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task GetPageAccessibilityAsync_WithInvalidPageName_ShouldReturnNullData()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/pages/page?PageName=NonExistentPage");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<PageDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeNull();
    }

    #endregion

    #region GetAllPagesAccessibility

    [Fact]
    public async Task GetAllPagesAccessibilityAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Act
        var httpResponse = await _factory.CreateClient().GetAsync($"{_baseUrl}/pages");

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetAllPagesAccessibilityAsync_WithAuthorization_ShouldReturnPages()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/pages");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<List<PageDto>>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
    }

    #endregion

    #region GetLocalizedContents

    [Fact]
    public async Task GetLocalizedContents_WithoutAuthorization_ShouldReturnContents()
    {
        // Act - This endpoint allows anonymous access
        var httpResponse = await _factory.CreateClient().GetAsync($"{_baseUrl}/localizedContents");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<List<LocalizedContentDto>>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.StatusCode.Should().Be((int)HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetLocalizedContents_ShouldReturnLocalizedContentList()
    {
        // Act
        var httpResponse = await _factory.CreateClient().GetAsync($"{_baseUrl}/localizedContents");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<List<LocalizedContentDto>>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeNull();
    }

    #endregion
}
