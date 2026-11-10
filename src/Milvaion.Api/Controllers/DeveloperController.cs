using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Milvaion.Application.Utils.Attributes;
using Milvasoft.Components.Rest.MilvaResponse;

namespace Milvaion.Api.Controllers;

/// <summary>
/// Developer endpoints.
/// </summary>
/// <remarks>
/// Only registered when <c>MilvaionConfig:Developer:EndpointsEnabled</c> is true, see
/// <see cref="AppStartup.DeveloperEndpointsFeatureProvider"/>. Even then everything except the bootstrap requires
/// super admin.
/// </remarks>
[ApiController]
[Route(GlobalConstant.FullRoute)]
[ApiVersion(GlobalConstant.CurrentApiVersion)]
[ApiExplorerSettings(GroupName = "v1.0")]
[Auth(PermissionCatalog.App.SuperAdmin)]
public class DeveloperController(IDeveloperService developerService) : ControllerBase
{
    private readonly IDeveloperService _developerService = developerService;

    /// <summary>
    /// Remove, recreates and seed database for development purposes.
    /// </summary>
    /// <returns></returns>
    [HttpPost("database/reset")]
    public Task<Response> MigrateAsync() => _developerService.ResetDatabaseAsync();

    /// <summary>
    /// Seeds data for development purposes.
    /// </summary>
    /// <returns></returns>
    [HttpPost("database/seed")]
    public Task<Response> SeedDataAsync() => _developerService.SeedDevelopmentDataAsync();

    /// <summary>
    /// Seeds fake data for development purposes.
    /// </summary>
    /// <returns></returns>
    [HttpPost("database/seed/fake")]
    public Task<Response> SeedFakeDataAsync(bool sameData = true, string locale = "tr") => _developerService.SeedFakeDataAsync(sameData, locale);

    /// <summary>
    /// Initial migration operation. Anonymous because no user exists yet the first time it runs; it does nothing
    /// on an initialized database.
    /// </summary>
    /// <returns></returns>
    [AllowAnonymous]
    [HttpPost("database/init")]
    public async Task<Response> InitDatabaseAsync() => await _developerService.InitDatabaseAsync();

    /// <summary>
    /// Resets ui related data.
    /// </summary>
    /// <returns></returns>
    [HttpPost("database/reset/ui")]
    public async Task<Response> ResetUIRelatedDataAsync() => await _developerService.ResetUIRelatedDataAsync();

    /// <summary>
    /// Exports product related data.
    /// </summary>
    /// <returns></returns>
    [HttpGet("export/productRelatedData")]
    public Task<Response> ExportExistingDataAsync() => _developerService.ExportExistingDataAsync();

    /// <summary>
    /// Imports exported product related data.
    /// </summary>
    /// <returns></returns>
    [HttpGet("import/productRelatedData")]
    public Task<Response> ImportExistingDataAsync() => _developerService.ImportExistingDataAsync();
}