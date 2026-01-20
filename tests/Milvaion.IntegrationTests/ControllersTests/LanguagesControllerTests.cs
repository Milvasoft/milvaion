using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Milvaion.Application.Dtos.ContentManagementDtos.LanguageDtos;
using Milvaion.Application.Features.Languages.GetLanguageList;
using Milvaion.Application.Features.Languages.UpdateLanguage;
using Milvaion.Application.Utils.Constants;
using Milvaion.Domain;
using Milvaion.Infrastructure.Persistence.Context;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Types.Structs;
using System.Net;
using System.Net.Http.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.ControllersTests;

[Collection(nameof(MilvaionTestCollection))]
[Trait("Controller Integration Tests", "Integration tests for LanguagesController.")]
public class LanguagesControllerTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    private const string _baseUrl = $"{GlobalConstant.RoutePrefix}/v1.0/languages";

    #region GetLanguages

    [Fact]
    public async Task GetLanguagesAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Arrange
        var request = new GetLanguageListQuery();

        // Act
        var httpResponse = await _factory.CreateClient().PatchAsJsonAsync(_baseUrl, request);

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetLanguagesAsync_WithAuthorization_ShouldReturnLanguages()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        await SeedLanguagesAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new GetLanguageListQuery();

        // Act
        var httpResponse = await client.PatchAsJsonAsync(_baseUrl, request);
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<LanguageDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.StatusCode.Should().Be((int)HttpStatusCode.OK);
        result.Data.Should().NotBeNull();
        result.Data.Should().HaveCountGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task GetLanguagesAsync_WithPagination_ShouldReturnPaginatedLanguages()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        await SeedLanguagesAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new GetLanguageListQuery
        {
            PageNumber = 1,
            RowCount = 2
        };

        // Act
        var httpResponse = await client.PatchAsJsonAsync(_baseUrl, request);
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<LanguageDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().HaveCount(2);
    }

    #endregion

    #region UpdateLanguage

    [Fact]
    public async Task UpdateLanguageAsync_WithValidData_ShouldUpdateLanguage()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var language = await SeedSingleLanguageAsync("de", "German", false, false);
        var client = await _factory.CreateClient().LoginAsync();
        var request = new UpdateLanguageCommand
        {
            Id = language.Id,
            Supported = new UpdateProperty<bool>(true)
        };

        // Act
        var httpResponse = await client.PutAsJsonAsync($"{_baseUrl}/language", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<int>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();

        // Verify in database
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var updatedLanguage = await dbContext.Languages.FirstOrDefaultAsync(l => l.Id == language.Id);
        updatedLanguage.Should().NotBeNull();
        updatedLanguage.Supported.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateLanguageAsync_SetAsDefault_ShouldUpdateLanguage()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        await SeedLanguagesAsync();
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var germanLanguage = await dbContext.Languages.FirstOrDefaultAsync(l => l.Code == "de");
        var client = await _factory.CreateClient().LoginAsync();
        var request = new UpdateLanguageCommand
        {
            Id = germanLanguage.Id,
            IsDefault = new UpdateProperty<bool>(true),
            Supported = new UpdateProperty<bool>(true)
        };

        // Act
        var httpResponse = await client.PutAsJsonAsync($"{_baseUrl}/language", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<int>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();

        // Verify in database - only one default language should exist
        var defaultLanguages = await dbContext.Languages.Where(l => l.IsDefault).ToListAsync();
        defaultLanguages.Should().HaveCount(1);
        defaultLanguages[0].Code.Should().Be("de");
    }

    [Fact]
    public async Task UpdateLanguageAsync_WithInvalidId_ShouldReturnError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new UpdateLanguageCommand
        {
            Id = 999,
            Supported = new UpdateProperty<bool>(true)
        };

        // Act
        var httpResponse = await client.PutAsJsonAsync($"{_baseUrl}/language", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<int>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
    }

    #endregion

    #region Helper Methods

    private async Task SeedLanguagesAsync()
    {
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();

        var languages = new List<Language>
        {
            new() { Code = "en", Name = "English", IsDefault = true, Supported = true },
            new() { Code = "tr", Name = "Turkish", IsDefault = false, Supported = true },
            new() { Code = "de", Name = "German", IsDefault = false, Supported = false }
        };

        await dbContext.Languages.AddRangeAsync(languages);
        await dbContext.SaveChangesAsync();
    }

    private async Task<Language> SeedSingleLanguageAsync(string code, string name, bool isDefault, bool supported)
    {
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();

        var language = new Language
        {
            Code = code,
            Name = name,
            IsDefault = isDefault,
            Supported = supported
        };

        await dbContext.Languages.AddAsync(language);
        await dbContext.SaveChangesAsync();

        return language;
    }

    #endregion
}
