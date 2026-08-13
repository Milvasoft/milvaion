using FluentAssertions;
using Milvaion.Application.Dtos.SettingsDtos;
using Milvaion.Application.Utils.Constants;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Components.Rest.MilvaResponse;
using System.Net;
using System.Net.Http.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.ControllersTests;

[Collection(nameof(MilvaionTestCollection))]
[Trait("Controller Integration Tests", "Integration tests for SettingsController.")]
public class SettingsControllerTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    private const string _baseUrl = $"{GlobalConstant.RoutePrefix}/v1.0/settings";

    #region GetPublicSettings

    [Fact]
    public async Task GetPublicSettingsAsync_WithoutAuthorization_ShouldReturnAuthProviderFlags()
    {
        // Act - the public endpoint is anonymous and exposes the auth provider hints the login page needs.
        var httpResponse = await _factory.CreateClient().GetAsync($"{_baseUrl}/public");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<PublicSettingsDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeNull();

        // With no external identity provider configured, both providers report as disabled but are always present.
        result.Data.Oidc.Should().NotBeNull();
        result.Data.Oidc.Enabled.Should().BeFalse();
        result.Data.Ldap.Should().NotBeNull();
        result.Data.Ldap.Enabled.Should().BeFalse();
    }

    #endregion
}
