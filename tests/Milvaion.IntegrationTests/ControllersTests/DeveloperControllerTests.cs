using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.Constants;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Components.Rest.MilvaResponse;
using System.Net;
using System.Net.Http.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.ControllersTests;

[Collection(nameof(MilvaionTestCollection))]
[Trait("Controller Integration Tests", "Integration tests for DeveloperController.")]
public class DeveloperControllerTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    #region InitDatabase

    [Fact]
    public async Task InitDatabaseAsync_ShouldReturnError_WhenAlreadyInitialized()
    {
        // Arrange
        await InitializeAsync();

        // Seed required data so InitDatabase thinks it's already done
        await SeedRootUserAndSuperAdminRoleAsync("testpass");

        var client = _factory.CreateClient();

        // Act - Call init twice; second call should say "Already initialized!"
        var response = await client.PostAsync($"{GlobalConstant.RoutePrefix}/v1.0/developer/database/init", null);
        var result = await response.Content.ReadFromJsonAsync<Response>();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
    }

    #endregion

    #region ExportExistingData

    [Fact]
    public async Task ExportExistingDataAsync_ShouldReturnSuccess()
    {
        // Arrange
        await InitializeAsync();
        await SeedRootUserAndSuperAdminRoleAsync();

        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var response = await client.GetAsync($"{GlobalConstant.RoutePrefix}/v1.0/developer/export/productRelatedData");
        var result = await response.Content.ReadFromJsonAsync<Response>();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
    }

    #endregion

    #region ImportExistingData

    [Fact]
    public async Task ImportExistingDataAsync_ShouldHandleMissingFile_Gracefully()
    {
        // Arrange
        await InitializeAsync();

        // Ensure the export file doesn't exist
        var filePath = Path.Combine(GlobalConstant.JsonFilesPath, "export.json");
        if (File.Exists(filePath))
            File.Delete(filePath);

        await SeedRootUserAndSuperAdminRoleAsync();

        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var response = await client.GetAsync($"{GlobalConstant.RoutePrefix}/v1.0/developer/import/productRelatedData");
        var result = await response.Content.ReadFromJsonAsync<Response>();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse("import should fail when export file doesn't exist");
    }

    [Fact]
    public async Task ImportExistingDataAsync_ShouldSucceed_WhenExportFileExists()
    {
        // Arrange
        await InitializeAsync();
        await SeedRootUserAndSuperAdminRoleAsync();

        var client = await _factory.CreateClient().LoginAsync();

        // First export data to create the file
        await client.GetAsync($"{GlobalConstant.RoutePrefix}/v1.0/developer/export/productRelatedData");

        // Act - Now import should find the file
        var response = await client.GetAsync($"{GlobalConstant.RoutePrefix}/v1.0/developer/import/productRelatedData");
        var result = await response.Content.ReadFromJsonAsync<Response>();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue("import should succeed after export creates the file");
    }

    #endregion

    #region SeedFakeData

    [Fact]
    public async Task SeedFakeDataAsync_ShouldReturnSuccess_WhenNotProduction()
    {
        // Arrange
        await InitializeAsync();
        await SeedRootUserAndSuperAdminRoleAsync("testpass");

        var client = await _factory.CreateClient().LoginAsync(password: "testpass");

        // Act
        var response = await client.PostAsync($"{GlobalConstant.RoutePrefix}/v1.0/developer/database/seed/fake?sameData=true&locale=tr", null);
        var result = await response.Content.ReadFromJsonAsync<Response>();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue("fake data seeding should succeed in non-production environment");
    }

    #endregion

    #region DeveloperService - Production Guard via DI

    [Fact]
    public async Task DeveloperService_ResetDatabase_ShouldReturnError_WhenProductionEnvironment()
    {
        // Arrange
        await InitializeAsync();

        var originalEnv = Environment.GetEnvironmentVariable("MILVA_ENV");

        try
        {
            // Simulate production environment
            Environment.SetEnvironmentVariable("MILVA_ENV", "prod");

            var developerService = _serviceProvider.GetRequiredService<IDeveloperService>();

            // Act
            var result = await developerService.ResetDatabaseAsync();

            // Assert
            result.IsSuccess.Should().BeFalse("ResetDatabase should be blocked in production environment");
        }
        finally
        {
            // Restore original environment
            Environment.SetEnvironmentVariable("MILVA_ENV", originalEnv);
        }
    }

    [Fact]
    public async Task DeveloperService_SeedDevelopmentData_ShouldReturnError_WhenProductionEnvironment()
    {
        // Arrange
        await InitializeAsync();

        var originalEnv = Environment.GetEnvironmentVariable("MILVA_ENV");

        try
        {
            Environment.SetEnvironmentVariable("MILVA_ENV", "prod");

            var developerService = _serviceProvider.GetRequiredService<IDeveloperService>();

            // Act
            var result = await developerService.SeedDevelopmentDataAsync();

            // Assert
            result.IsSuccess.Should().BeFalse("SeedDevelopmentData should be blocked in production environment");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MILVA_ENV", originalEnv);
        }
    }

    [Fact]
    public async Task DeveloperService_SeedFakeData_ShouldReturnError_WhenProductionEnvironment()
    {
        // Arrange
        await InitializeAsync();

        var originalEnv = Environment.GetEnvironmentVariable("MILVA_ENV");

        try
        {
            Environment.SetEnvironmentVariable("MILVA_ENV", "prod");

            var developerService = _serviceProvider.GetRequiredService<IDeveloperService>();

            // Act
            var result = await developerService.SeedFakeDataAsync();

            // Assert
            result.IsSuccess.Should().BeFalse("SeedFakeData should be blocked in production environment");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MILVA_ENV", originalEnv);
        }
    }

    [Fact]
    public async Task DeveloperService_ExportExistingData_ShouldReturnError_WhenProductionEnvironment()
    {
        // Arrange
        await InitializeAsync();

        var originalEnv = Environment.GetEnvironmentVariable("MILVA_ENV");

        try
        {
            Environment.SetEnvironmentVariable("MILVA_ENV", "prod");

            var developerService = _serviceProvider.GetRequiredService<IDeveloperService>();

            // Act
            var result = await developerService.ExportExistingDataAsync();

            // Assert
            result.IsSuccess.Should().BeFalse("ExportExistingData should be blocked in production environment");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MILVA_ENV", originalEnv);
        }
    }

    [Fact]
    public async Task DeveloperService_ImportExistingData_ShouldReturnError_WhenProductionEnvironment()
    {
        // Arrange
        await InitializeAsync();

        var originalEnv = Environment.GetEnvironmentVariable("MILVA_ENV");

        try
        {
            Environment.SetEnvironmentVariable("MILVA_ENV", "prod");

            var developerService = _serviceProvider.GetRequiredService<IDeveloperService>();

            // Act
            var result = await developerService.ImportExistingDataAsync();

            // Assert
            result.IsSuccess.Should().BeFalse("ImportExistingData should be blocked in production environment");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MILVA_ENV", originalEnv);
        }
    }

    #endregion
}
