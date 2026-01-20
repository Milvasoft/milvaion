using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.Attributes;
using Milvaion.Application.Utils.Constants;
using Milvasoft.Components.Rest.MilvaResponse;

namespace Milvaion.Api.Controllers;

/// <summary>
/// Developer endpoints.
/// </summary>
[ApiController]
[Route(GlobalConstant.FullRoute)]
[ApiVersion(GlobalConstant.CurrentApiVersion)]
[ApiExplorerSettings(GroupName = "v1.0")]
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
    /// Initial migration operation.
    /// </summary>
    /// <returns></returns>
    [HttpPost("database/init")]
    public async Task<Response> InitDatabaseAsync() => await _developerService.InitDatabaseAsync();

    /// <summary>
    /// Resets ui related data.
    /// </summary>
    /// <returns></returns>
    [Auth]
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