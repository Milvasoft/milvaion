using FluentAssertions;
using Milvaion.Application.Dtos.ApiKeyDtos;
using Milvaion.Application.Features.ApiKeys.CreateApiKey;
using Milvaion.Application.Features.ApiKeys.RevokeApiKey;
using Milvaion.Application.Utils.Constants;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Components.Rest.MilvaResponse;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.ControllersTests;

/// <summary>
/// Covers the api key life cycle against <c>/mcp</c>, the endpoint api keys exist for.
/// </summary>
[Collection(nameof(MilvaionTestCollection))]
[Trait("Controller Integration Tests", "Integration tests for api key authentication.")]
public class ApiKeyAuthenticationTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    private const string _apiKeysUrl = $"{GlobalConstant.RoutePrefix}/v1.0/apikeys";
    private const string _mcpInitialize = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"tests","version":"1"}}}""";

    [Fact]
    public async Task Mcp_WithoutApiKey_ShouldReturnUnauthorized()
    {
        // Act
        var httpResponse = await PostToMcpAsync(apiKey: null);

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Mcp_WithMalformedApiKey_ShouldReturnUnauthorized()
    {
        // Act
        var httpResponse = await PostToMcpAsync("not-a-real-key");

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Mcp_WithCreatedApiKey_ShouldAuthenticate_UntilItIsRevoked()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var admin = await _factory.CreateClient().LoginAsync();

        var createResponse = await admin.PostAsJsonAsync($"{_apiKeysUrl}/apiKey", new CreateApiKeyCommand { Name = "Integration test key" });
        var created = await createResponse.Content.ReadFromJsonAsync<Response<CreatedApiKeyDto>>();

        created.IsSuccess.Should().BeTrue();

        // Act
        var beforeRevoke = await PostToMcpAsync(created.Data.Key);

        var revokeResponse = await admin.PostAsJsonAsync($"{_apiKeysUrl}/apiKey/revoke", new RevokeApiKeyCommand { ApiKeyId = created.Data.Id });
        revokeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterRevoke = await PostToMcpAsync(created.Data.Key);

        // Assert
        beforeRevoke.StatusCode.Should().Be(HttpStatusCode.OK);
        afterRevoke.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private Task<HttpResponseMessage> PostToMcpAsync(string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(_mcpInitialize, Encoding.UTF8, "application/json")
        };

        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

        if (apiKey != null)
            request.Headers.Add(MessageConstant.ApiKey, apiKey);

        return _factory.CreateClient().SendAsync(request);
    }
}
