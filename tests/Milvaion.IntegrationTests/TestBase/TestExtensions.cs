using Milvaion.Application.Dtos.AccountDtos;
using Milvaion.Application.Features.Account.Login;
using Milvaion.Application.Utils.Constants;
using Milvasoft.Components.Rest.MilvaResponse;
using System.Net.Http.Json;

namespace Milvaion.IntegrationTests.TestBase;

public static class TestExtensions
{
    public static async Task<HttpClient> LoginAsync(this HttpClient client, string username = "rootuser", string password = "defaultpass", string deviceId = "device-id")
    {
        var request = new LoginCommand
        {
            UserName = username,
            Password = password,
            DeviceId = deviceId
        };

        // URL should match the lowercase route configuration
        var httpResponse = await client.PostAsJsonAsync($"{GlobalConstant.RoutePrefix}/v1/account/login", request);

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorContent = await httpResponse.Content.ReadAsStringAsync();

            throw new InvalidOperationException($"Login failed with status {httpResponse.StatusCode}: {errorContent}");
        }

        var loginResult = await httpResponse.Content.ReadFromJsonAsync<Response<LoginResponseDto>>();

        if (loginResult is not null && loginResult.IsSuccess)
            client.DefaultRequestHeaders.Add("Authorization", $"{loginResult.Data.Token.TokenType} {loginResult.Data.Token.AccessToken}");

        return client;
    }
}
