using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Milvaion.Application.Dtos.ExportDtos;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.Attributes;
using Milvaion.Application.Utils.Constants;
using System.Net;

namespace Milvaion.Api.Controllers;

/// <summary>
/// Export endpoints.
/// </summary>
[ApiController]
[Route(GlobalConstant.FullRoute)]
[ApiVersion(GlobalConstant.CurrentApiVersion)]
[ApiExplorerSettings(GroupName = "v1.0")]
public class ExportsController(IExportService exportService) : ControllerBase
{
    private readonly IExportService _exportService = exportService;

    /// <summary>
    /// Dynamically creates an excel file according to the export type.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth]
    [HttpPost("export")]
    [ProducesDefaultResponseType(typeof(FileResult))]
    [ProducesResponseType(typeof(FileResult), 200)]
    [ProducesResponseType(typeof(NotFoundResult), 400)]
    public async Task<IActionResult> ExportProductsToExcelAsync(ExportRequest request, CancellationToken cancellation)
    {
        var fileResponse = await _exportService.DynamicExportToExcelAsync(request, cancellation);

        if (fileResponse.Data?.FileStream == null)
        {
            fileResponse.Data = null;
            return Ok(fileResponse);
        }

        // Ignroing response logging for file download.
        HttpContext.Items.Add(GlobalConstant.IgnoreResponseLoggingItemsKey, true);
        Response.ContentType = fileResponse.Data.MimeType;
        Response.StatusCode = (int)HttpStatusCode.OK;

        return File(fileResponse.Data.FileStream, fileResponse.Data.MimeType, fileResponse.Data.FileName);
    }
}