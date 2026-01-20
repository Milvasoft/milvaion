using Milvaion.Application.Dtos.ExportDtos;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Interfaces;

/// <summary>
/// Used to export data. 
/// </summary>
public interface IExportService : IInterceptable
{
    /// <summary>
    /// Dynamically creates an excel file according to the export type.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<Response<ExportResult>> DynamicExportToExcelAsync(ExportRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Exports the data to excel.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="response"></param>
    /// <param name="pageName"></param>
    /// <returns></returns>
    MemoryStream ExportToExcel<T>(ListResponse<T> response, string pageName = null) where T : class;

    /// <summary>
    /// Exports the data to excel.
    /// </summary>
    /// <param name="hasMetadataResponse"></param>
    /// <param name="pageName"></param>
    /// <returns></returns>
    MemoryStream ExportToExcel(IHasMetadata hasMetadataResponse, string pageName = null);
}
